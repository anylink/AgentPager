using System.Text.Json;
using System.Threading.Channels;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed record BridgeViewState(
    string ServiceStatus,
    int PhoneCount,
    bool HookInstalled,
    DateTimeOffset? LastHookAt,
    string? LastError,
    string PairingText,
    string SelectedAddress,
    List<NetworkAddressOption> Networks,
    int TaskCount);

public sealed class BridgeRuntime : IAsyncDisposable
{
    private abstract record BridgeEvent;
    private sealed record HookEvent(HookEnvelope Envelope) : BridgeEvent;
    private sealed record RolloutEvent(List<CodexRolloutSignal> Signals) : BridgeEvent;
    private sealed record ControlEvent(string Text) : BridgeEvent;
    private sealed record PhoneCountEvent(int Count) : BridgeEvent;
    private sealed record UsageEvent(UsageSnapshot? Usage) : BridgeEvent;
    private sealed record MaintenanceEvent : BridgeEvent;

    private readonly DailyFileLog _log = new();
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskSnapshotPersistence _persistence = new();
    private readonly CodexSessionTitleReader _titleReader = new();
    private readonly CodexRolloutObserver _rollout = new();
    private readonly CodexUsageLoader _usageLoader = new();
    private readonly CodexHookConfiguration _hookConfiguration = new();
    private readonly ReplayGuard _replayGuard = new();
    private readonly byte[] _pairingSecret;
    private readonly TaskCatalog _catalog;
    private HookTcpServer? _hookServer;
    private LanBridgeServer? _lanServer;
    private CancellationTokenSource? _linkedCancellation;
    private Task? _eventTask;
    private Task? _pollTask;
    private Task? _usageTask;
    private UsageSnapshot? _usage;
    private DateTimeOffset? _lastHookAt;
    private string _serviceStatus = "正在启动";
    private string? _lastError;
    private int _phoneCount;
    private string _selectedAddress;
    private List<NetworkAddressOption> _networks;

    public BridgeRuntime()
    {
        Settings = BridgeSettings.Load();
        _networks = NetworkAddresses.Available();
        _selectedAddress = _networks.FirstOrDefault(value => value.Address == Settings.SelectedNetworkAddress)?.Address
            ?? _networks.FirstOrDefault()?.Address ?? "127.0.0.1";
        Settings.SelectedNetworkAddress = _selectedAddress;
        _pairingSecret = new PairingSecretStore().LoadOrCreate();
        _catalog = new(_persistence.Load());
        _catalog.ApplyTitles(_titleReader.Load());
        RegisterAgents();
        State = CreateState();
    }

    /// <summary>
    /// 把所有 Agent Adapter 注册到全局 <see cref="AgentRegistry"/>。
    /// 调用方（UI 层 / TrayController / BridgeRuntime）通过 AgentRegistry.All
    /// 获取当前支持的全部 Agent，无需硬编码 agent 列表。
    ///
    /// PR2 范围：仅注册 Codex。后续 PR 按 docs/agent-adapter-guide.md
    /// 增加 ClaudeCode / Codebuddy / OpenCode。
    /// </summary>
    private void RegisterAgents()
    {
        AgentRegistry.Register(new CodexAgentAdapter(_hookConfiguration));
    }

    public BridgeSettings Settings { get; }
    public BridgeViewState State { get; private set; }
    public event Action? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _linkedCancellation.Token;
        _eventTask = EventLoop(token);
        try
        {
            _hookServer = new HookTcpServer(
                hook => _events.Writer.WriteAsync(new HookEvent(hook), token).AsTask(), _log);
            _hookServer.Start();
            _lanServer = new(
                PairingText,
                SnapshotText,
                text => _events.Writer.WriteAsync(new ControlEvent(text), token).AsTask(),
                count => _events.Writer.TryWrite(new PhoneCountEvent(count)),
                _log);
            await _lanServer.StartAsync(token);
            _serviceStatus = "局域网服务运行中";
            _log.Write("INFO", "Bridge 服务已启动。");
        }
        catch (Exception error)
        {
            _serviceStatus = "服务启动失败";
            _lastError = error.Message;
            _log.Write("ERROR", error.ToString());
        }
        PublishState();
        _pollTask = PollLoop(token);
        _usageTask = UsageLoop(token);
    }

    public void InstallHook(IAgentAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var (success, agentName, error) = RunAdapterHook(adapter, install: true, out var result);
        if (success)
        {
            _log.Write("INFO", result.Changed
                ? $"{agentName} Hook 已安装。"
                : $"{agentName} Hook 已是最新状态。");
            _lastError = null;
        }
        else
        {
            _lastError = error is null
                ? "找不到可用的 Agent Adapter。"
                : $"安装 Hook 失败：{error}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void UninstallHook(IAgentAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var (success, agentName, error) = RunAdapterHook(adapter, install: false, out var result);
        if (success)
        {
            _log.Write("INFO", $"{agentName} Hook 已移除。");
            _lastError = null;
        }
        else
        {
            _lastError = error is null
                ? "找不到可用的 Agent Adapter。"
                : $"卸载 Hook 失败：{error}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    /// <summary>
    /// 保留 Codex 默认入口：默认从 <see cref="AgentRegistry"/> 取 CodexCLI Adapter 调用。
    /// 供主窗口 / 测试代码无 Adapter 引用时使用。
    /// </summary>
    public void InstallHook() => InstallHook(AgentRegistry.Get(AgentSource.CodexCLI)
        ?? throw new InvalidOperationException("AgentRegistry 未注册 Codex Agent Adapter"));

    public void UninstallHook() => UninstallHook(AgentRegistry.Get(AgentSource.CodexCLI)
        ?? throw new InvalidOperationException("AgentRegistry 未注册 Codex Agent Adapter"));

    /// <summary>
    /// 通过 <see cref="IAgentAdapter"/> 完成 Hook 安装 / 卸载。返回 (success, agentName, error)。
    /// </summary>
    private (bool success, string agentName, string? error) RunAdapterHook(
        IAgentAdapter adapter,
        bool install,
        out HookInstallResult result)
    {
        var agentName = adapter.Descriptor.DisplayName;
        result = install
            ? adapter.Install(Environment.ProcessPath!)
            : adapter.Uninstall();
        return (result.Success, agentName, result.Error);
    }

    public void SelectAddress(string address)
    {
        if (_networks.All(value => value.Address != address)) return;
        _selectedAddress = address;
        Settings.SelectedNetworkAddress = address;
        Settings.Save();
        PublishState();
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        Settings.LaunchAtLogin = enabled;
        Settings.Save();
        PublishState();
    }

    public void RefreshNetworks()
    {
        _networks = NetworkAddresses.Available();
        if (_networks.All(value => value.Address != _selectedAddress))
            _selectedAddress = _networks.FirstOrDefault()?.Address ?? "127.0.0.1";
        Settings.SelectedNetworkAddress = _selectedAddress;
        Settings.Save();
        PublishState();
    }

    private async Task EventLoop(CancellationToken cancellationToken)
    {
        await foreach (var value in _events.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var changed = false;
                switch (value)
                {
                    case HookEvent hook:
                        changed = HandleHook(hook.Envelope);
                        break;
                    case RolloutEvent rollout:
                        changed = _catalog.Accept(rollout.Signals);
                        break;
                    case ControlEvent control:
                        changed = await HandleControl(control.Text);
                        break;
                    case PhoneCountEvent count:
                        _phoneCount = count.Count;
                        changed = true;
                        break;
                    case UsageEvent usage:
                        _usage = usage.Usage;
                        changed = true;
                        break;
                    case MaintenanceEvent:
                        changed = _catalog.Maintain();
                        changed = _catalog.ApplyTitles(_titleReader.Load()) || changed;
                        break;
                }
                if (!changed) continue;
                SaveAndBroadcast();
            }
            catch (Exception error)
            {
                _lastError = error.Message;
                _log.Write("ERROR", error.ToString());
                PublishState();
            }
        }
    }

    /// <summary>
    /// PR3 起：Hook 处理路径接收通用 <see cref="HookEnvelope"/>。
    /// 路由到对应 <see cref="IAgentAdapter.ReduceHook"/> 得到内部 hook 信号，
    /// 然后按信号类型分发到 reducer：
    /// - <see cref="CodexHookSignal"/>：走现有 Codex rollout + reducer 路径
    /// - 未来：<c>ClaudeCodeHookSignal</c> / <c>CodebuddyHookSignal</c> / ...
    /// </summary>
    private bool HandleHook(HookEnvelope envelope)
    {
        var adapter = AgentRegistry.Get(envelope.Source);
        if (adapter is null)
        {
            _log.Write("WARN", $"收到未注册的 AgentSource={envelope.Source} 的 hook，已丢弃。");
            return false;
        }

        var signal = adapter.ReduceHook(envelope);
        _lastHookAt = DateTimeOffset.Now;
        switch (signal)
        {
            case CodexHookSignal codex:
                _rollout.Include(codex.Payload);
                return _catalog.Accept(codex.Payload);
            case null:
                _log.Write("WARN", $"Adapter {adapter.Descriptor.DisplayName} 返回了 null signal，可能未实现此事件处理。");
                return false;
            default:
                _log.Write("WARN", $"收到未支持的 AgentHookSignal 类型：{signal.GetType().Name}");
                return false;
        }
    }

    private async Task<bool> HandleControl(string text)
    {
        SignedControlEnvelope? request;
        try { request = JsonSerializer.Deserialize<SignedControlEnvelope>(text, WireJson.Options); }
        catch (JsonException) { return false; }
        if (request is null) return false;
        ControlAckPayload acknowledgment;
        try
        {
            _replayGuard.Validate(request, _pairingSecret);
            acknowledgment = _catalog.Perform(request.MessageId, request.Payload,
                _hookServer ?? throw new InvalidOperationException("Hook 通道尚未就绪"));
        }
        catch (ProtocolException)
        {
            acknowledgment = new(request.MessageId, ControlResult.Rejected, "签名或序号无效");
        }
        var envelope = MessageEnvelope<ControlAckPayload>.Create("control.ack", acknowledgment);
        if (_lanServer is not null)
            await _lanServer.BroadcastAsync(JsonSerializer.Serialize(envelope, WireJson.Options));
        return acknowledgment.Result == ControlResult.Accepted;
    }

    private async Task PollLoop(CancellationToken cancellationToken)
    {
        var maintenanceCounter = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(750, cancellationToken);
                var signals = _rollout.Observe();
                if (signals.Count > 0) await _events.Writer.WriteAsync(new RolloutEvent(signals), cancellationToken);
                if (++maintenanceCounter >= 4)
                {
                    maintenanceCounter = 0;
                    await _events.Writer.WriteAsync(new MaintenanceEvent(), cancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task UsageLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var usage = await Task.Run(() => _usageLoader.Load(), cancellationToken);
                await _events.Writer.WriteAsync(new UsageEvent(usage), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error)
            {
                _log.Write("ERROR", $"读取 Codex 额度失败：{error}");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SaveAndBroadcast()
    {
        var projection = _catalog.Projection();
        try { _persistence.Save(projection.Tasks); }
        catch (IOException error)
        {
            _lastError = $"保存任务状态失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
        if (_lanServer is not null) _ = _lanServer.BroadcastAsync(SnapshotText());
    }

    private string PairingText() =>
        JsonSerializer.Serialize(new PairingPayload(
            1,
            $"agentgrid-{Environment.MachineName}",
            _selectedAddress,
            49362,
            Convert.ToBase64String(_pairingSecret)), WireJson.Options);

    private string SnapshotText()
    {
        var projection = _catalog.Projection();
        var tasks = projection.Tasks.Select(task => task with
        {
            LatestStep = ToolStepSanitizer.Sanitize(task.LatestStep),
            Subagents = task.Subagents.Select(subagent =>
                subagent with { LatestStep = ToolStepSanitizer.Sanitize(subagent.LatestStep) }).ToList(),
        }).ToList();
        var payload = new StateSnapshotPayload(tasks, _usage, projection.FocusedTaskID, projection.PendingRequests);
        return JsonSerializer.Serialize(MessageEnvelope<StateSnapshotPayload>.Create("state.snapshot", payload), WireJson.Options);
    }

    private BridgeViewState CreateState() => new(
        _serviceStatus,
        _phoneCount,
        _hookConfiguration.IsInstalled(),
        _lastHookAt,
        _lastError,
        PairingText(),
        _selectedAddress,
        _networks,
        _catalog.Projection().Tasks.Count);

    private void PublishState()
    {
        State = CreateState();
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _linkedCancellation?.Cancel();
        if (_pollTask is not null) try { await _pollTask; } catch (OperationCanceledException) { }
        if (_usageTask is not null) try { await _usageTask; } catch (OperationCanceledException) { }
        if (_hookServer is not null) await _hookServer.DisposeAsync();
        if (_lanServer is not null) await _lanServer.DisposeAsync();
        _events.Writer.TryComplete();
        if (_eventTask is not null) try { await _eventTask; } catch (OperationCanceledException) { }
        _linkedCancellation?.Dispose();
    }
}

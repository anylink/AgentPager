using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentPager.Core;

namespace AgentPager.Bridge;

/// <summary>
/// PR3 起：HookTcpServer 完全 Agent 无关，只负责"按行接收 + source 路由"。
/// 收到的 JSON line 先解析 source 字段，从 <see cref="AgentRegistry"/> 找到对应
/// <see cref="IAgentAdapter"/>，由 adapter.ReduceHook 归约成内部信号。
///
/// 当前仅 Codex 路径有完整实现（通过 <see cref="CodexHookSignal"/> 包回
/// <see cref="CodexHookPayload"/>，再走现有 _pending 权限通道）。
/// 其他 agent 路径在 PR4+（ClaudeCodeAgentAdapter / CodebuddyAgentAdapter / ...）
/// 完成后增量启用。
/// </summary>
public sealed class HookTcpServer(Func<HookEnvelope, Task> hookHandler, DailyFileLog log)
    : ICodexPermissionResolver, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TcpClient> _pending = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;

    public void Start()
    {
        _cancellation = new();
        _listener = new(IPAddress.Loopback, 49361);
        _listener.Start();
        _acceptTask = AcceptLoop(_cancellation.Token);
    }

    public bool Resolve(string sessionID, CodexPermissionDecision decision)
    {
        if (!_pending.TryRemove(sessionID, out var client)) return false;
        try
        {
            using (client)
            {
                var response = CodexHookOutput.Permission(decision);
                client.GetStream().Write(response);
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Handle(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException error) { log.Write("ERROR", $"Hook accept: {error.Message}"); }
        }
    }

    private async Task Handle(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) { client.Dispose(); return; }

            // 1. 解析 source 字段，得到 AgentSource（Agent 无关的路由入口）
            var source = ExtractSource(line);
            if (source is null)
            {
                log.Write("WARN", "Hook 请求缺少 source 字段或该 source 未注册，已丢弃。");
                client.Dispose();
                return;
            }

            // 2. 找 adapter（避免重复反序列化）
            var adapter = AgentRegistry.Get(source.Value);
            if (adapter is null)
            {
                log.Write("WARN", $"Hook source={source} 未注册 adapter，已丢弃。");
                client.Dispose();
                return;
            }

            // 3. 构造 envelope 并交给 handler（handler 是 BridgeRuntime，
            //    内部会用 adapter.ReduceHook 还原成 CodexHookPayload / 其他）。
            using var doc = JsonDocument.Parse(line);
            var envelope = new HookEnvelope(
                Source: source.Value,
                HookEventName: doc.RootElement.TryGetProperty("hook_event_name", out var h)
                    ? h.GetString() ?? "" : "",
                SessionId: doc.RootElement.TryGetProperty("session_id", out var s)
                    ? s.GetString() ?? "" : "",
                Cwd: doc.RootElement.TryGetProperty("cwd", out var c)
                    ? c.GetString() ?? "" : "",
                Raw: doc.RootElement.Clone(),
                ToolName: doc.RootElement.TryGetProperty("tool_name", out var t)
                    ? t.GetString() : null,
                ToolUseId: doc.RootElement.TryGetProperty("tool_use_id", out var tu)
                    ? tu.GetString() : null,
                Prompt: doc.RootElement.TryGetProperty("prompt", out var p)
                    ? p.GetString() : null,
                TranscriptPath: doc.RootElement.TryGetProperty("transcript_path", out var tp)
                    ? tp.GetString() : null,
                Model: doc.RootElement.TryGetProperty("model", out var m)
                    ? m.GetString() : null
            );

            // 4. Codex 路径：保留 _pending permission 决策通道（避免客户端超时）。
            //    其他 agent 暂未实现权限请求，未来 PR 启用。
            if (source.Value is AgentSource.CodexDesktop or AgentSource.CodexCLI)
            {
                var hookEventName = envelope.HookEventName;
                var sessionId = envelope.SessionId;
                if (hookEventName == nameof(CodexHookEventName.PermissionRequest) && sessionId.Length > 0)
                {
                    if (_pending.TryGetValue(sessionId, out var previous)) previous.Dispose();
                    _pending[sessionId] = client;
                    await hookHandler(envelope);
                    return;
                }

                await hookHandler(envelope);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                client.Dispose();
                return;
            }

            // 5. 非 Codex agent：路由但不持有 connection（它们目前没有 permission 通道）
            await hookHandler(envelope);
            client.Dispose();
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
        {
            client.Dispose();
            log.Write("WARN", $"Hook 请求失败：{error.Message}");
        }
    }

    /// <summary>
    /// 从原始 JSON line 中提取 <c>source</c> 字段，得到 <see cref="AgentSource"/>。
    /// 解析失败或字段缺失时返回 null。
    /// </summary>
    private static AgentSource? ExtractSource(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("source", out var sourceProp))
                return null;
            // Codex 当前 payload 不带 source 字段，但 Codex 是默认 agent——
            // 历史兼容：缺失 source 字段时按 CodexCLI 路由。
            // 新 agent（ClaudeCode / Codebuddy / OpenCode）必须显式携带 source。
            var wireName = sourceProp.ValueKind == JsonValueKind.String
                ? sourceProp.GetString()
                : null;
            if (string.IsNullOrEmpty(wireName))
            {
                // 历史兼容：未带 source 视为 Codex CLI
                return AgentSource.CodexCLI;
            }
            return AgentRegistry.ResolveSource(wireName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        foreach (var client in _pending.Values) client.Dispose();
        _pending.Clear();
        if (_acceptTask is not null)
            try { await _acceptTask; } catch (OperationCanceledException) { }
        _cancellation?.Dispose();
    }
}
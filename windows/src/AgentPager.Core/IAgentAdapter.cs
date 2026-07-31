using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// Agent Hook 传输方式的分类。不同 Agent 的 Hook 协议差异较大：
/// - CommandHook：通过 CLI 子进程 stdin/stdout 通信（Codex / Claude Code / Codebuddy）
/// - HttpHook：通过本地 HTTP 端点同步阻塞响应（Codebuddy PermissionRequest）
/// - Plugin：通过宿主进程的插件机制（OpenCode plugin SDK）
/// </summary>
public enum HookTransportKind
{
    CommandHook,
    HttpHook,
    Plugin,
}

/// <summary>
/// Agent 静态描述信息（品牌、UI 资源）。手机端用它渲染任务卡片右上角的来源标识。
/// </summary>
public sealed record AgentDescriptor(
    AgentSource Source,
    string DisplayName,
    string ShortName,
    uint BrandColorArgb,
    string IconKey
);

/// <summary>
/// Hook 安装结果。
/// </summary>
public sealed record HookInstallResult(bool Changed, string? BackupPath = null, string? Error = null)
{
    public bool Success => Error is null;

    public static HookInstallResult NoChange() => new(false, null, null);
    public static HookInstallResult Updated(string? backup) => new(true, backup, null);
    public static HookInstallResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// 通用 Hook 入站信封。屏蔽不同 Agent 的 payload 字段差异，
/// 让 HookTcpServer 解析一次后路由给对应的 <see cref="IAgentAdapter"/>。
///
/// 注意：这是【阶段 1 引入】的通用类型。当前 <see cref="CodexHookPayload"/> 仍由
/// Codex 专属代码使用，下一个 PR 完成 Codex → Adapter 迁移后会替换为 <see cref="HookEnvelope"/>。
/// </summary>
public sealed record HookEnvelope(
    AgentSource Source,
    string HookEventName,
    string SessionId,
    string Cwd,
    JsonElement Raw,
    string? ToolName = null,
    string? ToolUseId = null,
    string? Prompt = null,
    string? TranscriptPath = null,
    string? Model = null
);

/// <summary>
/// Agent Adapter 接口。每种 Agent 的"Hook 配置安装 / 事件归约 / rollout 文件读取 /
/// 额度查询 / 标题读取"都被封装在这里，上层 <c>TaskCatalog</c> 完全不感知 Agent 差异。
///
/// 所有可选用方法（<see cref="ScanRollout"/>, <see cref="LoadUsage"/>, <see cref="ReadTitle"/>）
/// 默认返回 <c>null</c>，由具体 adapter 按需重写。这是阶段 1 的设计基线。
/// </summary>
public interface IAgentAdapter
{
    /// <summary>当前 Adapter 对应的 AgentSource。</summary>
    AgentSource Source { get; }

    /// <summary>静态描述信息（手机端 UI 渲染依据）。</summary>
    AgentDescriptor Descriptor { get; }

    /// <summary>Hook 传输方式，决定 Bridge 端的安装逻辑。</summary>
    HookTransportKind Transport { get; }

    /// <summary>判断当前 Agent 的 Hook 是否已注册到 AgentPager Bridge。</summary>
    bool IsInstalled();

    /// <summary>
    /// 安装 / 修复 Agent 的 Hook 配置。Bridge 在启动时会调用。
    /// 应当保留用户原有的 Hook 配置并自动备份（参考 <c>CodexHookConfiguration.Mutate</c> 模式）。
    /// </summary>
    /// <param name="bridgeExecutablePath">AgentPager Bridge 可执行文件绝对路径，Hook 会回调它。</param>
    HookInstallResult Install(string bridgeExecutablePath);

    /// <summary>移除 AgentPager 注入的 Hook，保留用户原有配置。</summary>
    HookInstallResult Uninstall();

    // ─────────────── 可选能力：rollout / 用量 / 标题 ───────────────
    // 这些方法默认返回 null，由具体 adapter 选择性重写。
    // 设计意图：Codex 已经有 rollout + usage 完整实现，Claude/Codebuddy 部分能力缺失，
    // OpenCode 通过 plugin 直接拿到，由 adapter 自行决定如何喂数据。

    /// <summary>
    /// 增量读取该 Agent 的会话进度信号（用于 Token 用量、子代理、用户提示）。
    /// 当前返回 <see cref="CodexRolloutSignal"/>（这是历史命名，下个 PR 完成
    /// Codex → Adapter 迁移时会统一重命名为 Agent 中性的 <c>RolloutSignal</c>）。
    /// </summary>
    IReadOnlyList<CodexRolloutSignal>? ScanRollout(DateTimeOffset since) => null;

    /// <summary>读取该 Agent 的用量窗口（5h / 周窗口）。</summary>
    UsageSnapshot? LoadUsage() => null;

    /// <summary>按 session id 读取任务标题（Codex 通过 session_index.jsonl 读取）。</summary>
    string? ReadTitle(string sessionId) => null;
}

/// <summary>
/// Adapter 注册表。Bridge 启动时由 <c>BridgeRuntime</c> 遍历 <see cref="All"/> 完成安装/卸载，
/// 用户 UI（菜单栏/系统托盘）也遍历 <see cref="All"/> 动态生成按钮，
/// 不再硬编码 agent 列表。
///
/// 线程模型：仅在启动期单线程访问，不做并发保护。
/// </summary>
public static class AgentRegistry
{
    private static readonly Dictionary<AgentSource, IAgentAdapter> _adapters = new();

    public static void Register(IAgentAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[adapter.Source] = adapter;
    }

    public static IReadOnlyList<IAgentAdapter> All
    {
        get
        {
            // 按 Source 枚举顺序输出，确保 UI 中顺序稳定
            return typeof(AgentSource).GetEnumValues()
                .Cast<AgentSource>()
                .Where(source => _adapters.ContainsKey(source))
                .Select(source => _adapters[source])
                .ToList();
        }
    }

    public static IAgentAdapter? Get(AgentSource source)
        => _adapters.TryGetValue(source, out var adapter) ? adapter : null;

    /// <summary>按 wire name 解析 AgentSource（"codexCLI" / "claudeCode" / ...）。
    /// 用于 Hook TCP Server 收到 payload 后按 source 字段路由。
    /// wire name 必须与 <see cref="WireName"/> 序列化映射保持一致。</summary>
    public static AgentSource? ResolveSource(string wireName)
    {
        if (string.IsNullOrEmpty(wireName)) return null;
        return wireName switch
        {
            "codexDesktop" => AgentSource.CodexDesktop,
            "codexCLI" => AgentSource.CodexCLI,
            "claudeCode" => AgentSource.ClaudeCode,
            "codebuddy" => AgentSource.Codebuddy,
            "openCode" => AgentSource.OpenCode,
            _ => null,
        };
    }

    /// <summary>测试用：清空注册表。</summary>
    internal static void ResetForTests() => _adapters.Clear();
}
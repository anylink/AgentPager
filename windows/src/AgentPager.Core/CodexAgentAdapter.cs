using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// Codex Agent Adapter —— 把现有 Codex 专属类（<see cref="CodexHookConfiguration"/>
/// / <see cref="CodexRolloutObserver"/> / <see cref="CodexUsageLoader"/> /
/// <see cref="CodexSessionTitleReader"/>）适配成 <see cref="IAgentAdapter"/> 协议。
///
/// 设计要点：
/// - 这是【组合（composition）】模式，不是【继承（inheritance）】模式：
///   Codex 类的实现不动，adapter 只做"转发 + 类型转换"。
/// - BridgeRuntime 内部继续直接持有 Codex 类做轮询（rollout / usage / title），
///   adapter 仅承担"用户可见的安装 / 卸载 / 状态查询"职责。
/// - 这样 PR2 范围内 UI 入口就完全数据驱动（AgentRegistry.All），但现有
///   轮询管线零改动。
///
/// 后续 PR 完成 Codex → Adapter 全面迁移时，本类会取代 BridgeRuntime 中的
/// _hookConfiguration / _titleReader / _rollout / _usageLoader 直接引用。
/// </summary>
public sealed class CodexAgentAdapter : IAgentAdapter
{
    /// <summary>Codex CLI 与 Desktop 共用 ~/.codex/hooks.json，因此一个 adapter
    /// 同时覆盖两个 <see cref="AgentSource"/>。UI 上它们都显示为"Codex"。</summary>
    public AgentSource Source => AgentSource.CodexCLI;

    public AgentDescriptor Descriptor => new(
        Source: AgentSource.CodexCLI,
        DisplayName: "Codex",
        ShortName: "CDX",
        BrandColorArgb: 0xFF5BC0BE,
        IconKey: "ic_agent_codex"
    );

    public HookTransportKind Transport => HookTransportKind.CommandHook;

    private readonly CodexHookConfiguration _hookConfiguration;

    /// <summary>
    /// 构造时注入现有的 Codex Hook 配置器。允许 BridgeRuntime 共享实例，
    /// 避免重复创建底层资源。
    /// </summary>
    public CodexAgentAdapter(CodexHookConfiguration hookConfiguration)
    {
        ArgumentNullException.ThrowIfNull(hookConfiguration);
        _hookConfiguration = hookConfiguration;
    }

    /// <summary>便捷构造器：从默认配置路径（~/.codex/hooks.json）创建 adapter。</summary>
    public CodexAgentAdapter() : this(new CodexHookConfiguration()) { }

    public bool IsInstalled() => _hookConfiguration.IsInstalled();

    public HookInstallResult Install(string bridgeExecutablePath)
    {
        try
        {
            var change = _hookConfiguration.Install(bridgeExecutablePath);
            return change.Changed
                ? HookInstallResult.Updated(change.BackupPath)
                : HookInstallResult.NoChange();
        }
        catch (Exception ex)
        {
            return HookInstallResult.Failure(ex.Message);
        }
    }

    public HookInstallResult Uninstall()
    {
        try
        {
            var change = _hookConfiguration.Uninstall();
            return change.Changed
                ? HookInstallResult.Updated(change.BackupPath)
                : HookInstallResult.NoChange();
        }
        catch (Exception ex)
        {
            return HookInstallResult.Failure(ex.Message);
        }
    }

    // ──── 可选能力：rollout / 用量 / 标题 ────
    // 当前由 BridgeRuntime 直接轮询底层 Codex 类（避免重复扫描），
    // 因此 adapter 在 PR2 范围内不重写这些方法。
    // 后续 PR 完成 Codex → Adapter 全面迁移时，会让 BridgeRuntime 改为调用
    // IAgentAdapter.ScanRollout / LoadUsage / ReadTitle，从而彻底替换直接持有。
}
using System.Text.Json;

namespace AgentPager.Core;

// =============================================================================
// Agent Adapter 实现模板
// -----------------------------------------------------------------------------
// 本文件是【未来添加新 Agent】时的参考骨架。实际编译时被 #if AGENT_ADAPTER_TEMPLATE
// 包裹，不会进入发布产物。删除本文件不影响项目构建。
//
// 使用方法：
//   1. 复制本文件，去掉 #if 包裹
//   2. 重命名为 <Agent>AgentAdapter.cs（如 ClaudeCodeAgentAdapter.cs）
//   3. 实现 Install / Uninstall / IsInstalled（参考 CodexHookConfiguration）
//   4. 在 BridgeRuntime.Start() 中调用 AgentRegistry.Register(new ClaudeCodeAgentAdapter(...))
//   5. 桌面端 UI（菜单栏/系统托盘）会自动出现该 Agent 的安装按钮
//
// 配合文档：docs/extensibility-refactor.md
// =============================================================================

#if AGENT_ADAPTER_TEMPLATE

/// <summary>
/// 示例 Agent Adapter 骨架。新加 Agent 时复制此文件修改。
/// </summary>
public sealed class ExampleAgentAdapter : IAgentAdapter
{
    // ──── AgentSource 必须先在 WireModels.cs 的 AgentSource 枚举中扩展 ────
    public AgentSource Source => AgentSource.ClaudeCode; // 改成新加的枚举值

    // ──── 描述符：手机端按它渲染任务卡片右上角的来源标识 ────
    public AgentDescriptor Descriptor => new(
        Source: Source,
        DisplayName: "Claude Code",
        ShortName: "CLD",
        BrandColorArgb: 0xFFD97757, // AARRGGBB
        IconKey: "ic_agent_claude"
    );

    // ──── Hook 传输方式 ────
    public HookTransportKind Transport => HookTransportKind.CommandHook;

    // ──── Hook 安装：参考 CodexHookConfiguration.Mutate 模式 ────
    public bool IsInstalled()
    {
        // TODO: 检查 ~/.<agent>/settings.json 是否已包含 AgentPager 的 hook
        // 参考：CodexHookConfiguration.IsInstalled
        return false;
    }

    public HookInstallResult Install(string bridgeExecutablePath)
    {
        try
        {
            // TODO: 1. 读取现有配置文件
            // TODO: 2. 合并 hook 配置（保留用户原有 hook！用 statusMessage 标记）
            // TODO: 3. 备份原文件
            // TODO: 4. 写入新配置
            // TODO: 5. 返回 HookInstallResult.Updated(backupPath) 或 NoChange()
            return HookInstallResult.NoChange();
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
            // TODO: 移除 statusMessage 为 "Managed by AgentPager" 的 hook 组
            return HookInstallResult.NoChange();
        }
        catch (Exception ex)
        {
            return HookInstallResult.Failure(ex.Message);
        }
    }

    // ──── 可选能力：rollout / 用量 / 标题 ────
    // 全部默认返回 null。下个 PR 实现具体 Agent 时按需重写。

    // public IReadOnlyList<CodexRolloutSignal>? ScanRollout(DateTimeOffset since)
    // {
    //     // TODO: 增量读取 ~/.<agent>/sessions/*.jsonl，转换为 CodexRolloutSignal
    //     return null;
    // }
    //
    // public UsageSnapshot? LoadUsage()
    // {
    //     // TODO: 读取 Agent 用量窗口（5h / 周）
    //     return null;
    // }
    //
    // public string? ReadTitle(string sessionId)
    // {
    //     // TODO: 从 Agent session index 读取任务标题
    //     return null;
    // }
}

#endif
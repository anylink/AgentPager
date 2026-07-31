import Foundation

/// Codex Agent Adapter —— 把现有 Codex 专属类适配成 `AgentAdapter` 协议。
///
/// 设计要点：
/// - 组合模式：Codex 类实现不动，adapter 只做转发 + 类型转换。
/// - BridgeModel 内部继续直接持有 Codex 类做轮询（rollout / usage / title），
///   adapter 仅承担"用户可见的安装 / 卸载 / 状态查询"职责。
public final class CodexAgentAdapter: AgentAdapter {
    /// Codex CLI 与 Desktop 共用 ~/.codex/hooks.json，因此一个 adapter
    /// 同时覆盖两个 `AgentSource`。UI 上它们都显示为"Codex"。
    public let source: AgentSource = .codexCLI

    public let descriptor = AgentDescriptor(
        source: .codexCLI,
        displayName: "Codex",
        shortName: "CDX",
        brandColorArgb: 0xFF5BC0BE,
        iconKey: "ic_agent_codex"
    )

    public let transport: HookTransportKind = .commandHook

    private let hookConfiguration: CodexHookConfiguration

    /// 构造时注入现有的 Codex Hook 配置器。允许 BridgeModel 共享实例，
    /// 避免重复创建底层资源。
    public init(hookConfiguration: CodexHookConfiguration) {
        self.hookConfiguration = hookConfiguration
    }

    /// 便捷构造器：从默认配置路径（~/.codex/hooks.json）创建 adapter。
    public convenience init() {
        self.init(hookConfiguration: CodexHookConfiguration())
    }

    public func isInstalled() -> Bool {
        hookConfiguration.isInstalled()
    }

    public func install(bridgeExecutablePath: String) throws -> HookInstallResult {
        do {
            let change = try hookConfiguration.install(command: bridgeExecutablePath)
            return change.changed
                ? .updated(backup: change.backupURL)
                : .noChange()
        } catch {
            return .failure(error.localizedDescription)
        }
    }

    public func uninstall() throws -> HookInstallResult {
        do {
            let change = try hookConfiguration.uninstall()
            return change.changed
                ? .updated(backup: change.backupURL)
                : .noChange()
        } catch {
            return .failure(error.localizedDescription)
        }
    }

    // 可选能力由 BridgeModel 内部继续直接调用底层 Codex 类，
    // adapter 在 PR2 范围内不重写 scanRollout / loadUsage / readTitle。
}
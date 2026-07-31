import Foundation

/// Agent Hook 传输方式的分类。不同 Agent 的 Hook 协议差异较大：
/// - CommandHook：通过 CLI 子进程 stdin/stdout 通信（Codex / Claude Code / Codebuddy）
/// - HttpHook：通过本地 HTTP 端点同步阻塞响应（Codebuddy PermissionRequest）
/// - Plugin：通过宿主进程的插件机制（OpenCode plugin SDK）
public enum HookTransportKind: String, Codable, Sendable {
    case commandHook = "commandHook"
    case httpHook = "httpHook"
    case plugin = "plugin"
}

/// Agent 静态描述信息（品牌、UI 资源）。手机端用它渲染任务卡片右上角的来源标识。
public struct AgentDescriptor: Equatable, Codable, Sendable {
    public let source: AgentSource
    public let displayName: String
    public let shortName: String
    /// 0xAARRGGBB
    public let brandColorArgb: UInt32
    public let iconKey: String

    public init(
        source: AgentSource,
        displayName: String,
        shortName: String,
        brandColorArgb: UInt32,
        iconKey: String
    ) {
        self.source = source
        self.displayName = displayName
        self.shortName = shortName
        self.brandColorArgb = brandColorArgb
        self.iconKey = iconKey
    }
}

/// Hook 安装结果。
public struct HookInstallResult: Equatable, Sendable {
    public let changed: Bool
    public let backupURL: URL?
    public let error: String?

    public var success: Bool { error == nil }

    public init(changed: Bool, backupURL: URL? = nil, error: String? = nil) {
        self.changed = changed
        self.backupURL = backupURL
        self.error = error
    }

    public static func noChange() -> HookInstallResult {
        HookInstallResult(changed: false)
    }

    public static func updated(backup: URL?) -> HookInstallResult {
        HookInstallResult(changed: true, backupURL: backup)
    }

    public static func failure(_ error: String) -> HookInstallResult {
        HookInstallResult(changed: false, backupURL: nil, error: error)
    }
}

/// 通用 Hook 入站信封。屏蔽不同 Agent 的 payload 字段差异，
/// 让 HookBridgeServer 解析一次后路由给对应的 `AgentAdapter`。
///
/// 注意：这是【阶段 1 引入】的通用类型。当前 `CodexHookPayload` 仍由
/// Codex 专属代码使用，下一个 PR 完成 Codex → Adapter 迁移后会替换为 `HookEnvelope`。
public struct HookEnvelope: Sendable {
    public let source: AgentSource
    public let hookEventName: String
    public let sessionId: String
    public let cwd: String
    public let raw: [String: Any]
    public let toolName: String?
    public let toolUseId: String?
    public let prompt: String?
    public let transcriptPath: String?
    public let model: String?

    public init(
        source: AgentSource,
        hookEventName: String,
        sessionId: String,
        cwd: String,
        raw: [String: Any],
        toolName: String? = nil,
        toolUseId: String? = nil,
        prompt: String? = nil,
        transcriptPath: String? = nil,
        model: String? = nil
    ) {
        self.source = source
        self.hookEventName = hookEventName
        self.sessionId = sessionId
        self.cwd = cwd
        self.raw = raw
        self.toolName = toolName
        self.toolUseId = toolUseId
        self.prompt = prompt
        self.transcriptPath = transcriptPath
        self.model = model
    }
}

/// Agent Adapter 协议。每种 Agent 的"Hook 配置安装 / 事件归约 / rollout 文件读取 /
/// 额度查询 / 标题读取"都被封装在这里，上层 TaskCatalog 完全不感知 Agent 差异。
///
/// 所有可选方法（scanRollout / loadUsage / readTitle）默认返回 nil，
/// 由具体 adapter 按需重写。这是阶段 1 的设计基线。
public protocol AgentAdapter {
    var source: AgentSource { get }
    var descriptor: AgentDescriptor { get }
    var transport: HookTransportKind { get }

    func isInstalled() -> Bool
    func install(bridgeExecutablePath: String) throws -> HookInstallResult
    func uninstall() throws -> HookInstallResult

    // ─────────────── 可选能力：rollout / 用量 / 标题 ───────────────
    func scanRollout(since: Date) -> [RolloutSignal]?
    func loadUsage() -> UsageSnapshot?
    func readTitle(sessionId: String) -> String?
}

extension AgentAdapter {
    public func scanRollout(since: Date) -> [CodexRolloutSignal]? { nil }
    public func loadUsage() -> UsageSnapshot? { nil }
    public func readTitle(sessionId: String) -> String? { nil }
}

/// Adapter 注册表。Bridge 启动时遍历 `all` 完成安装/卸载，
/// 用户 UI（菜单栏）也遍历 `all` 动态生成按钮，不再硬编码 agent 列表。
///
/// 线程模型：仅在启动期单线程访问，不做并发保护。
public enum AgentRegistry {
    private static var adapters: [AgentSource: any AgentAdapter] = [:]

    public static func register(_ adapter: any AgentAdapter) {
        adapters[adapter.source] = adapter
    }

    public static var all: [any AgentAdapter] {
        AgentSource.allCases.compactMap { adapters[$0] }
    }

    public static func get(_ source: AgentSource) -> (any AgentAdapter)? {
        adapters[source]
    }

    /// 按 wire name 解析 AgentSource（"codexCLI" / "claudeCode" / ...）。
    /// 用于 HookBridgeServer 收到 payload 后按 source 字段路由。
    public static func resolveSource(wireName: String) -> AgentSource? {
        adapters.values.first { adapter in
            adapter.descriptor.source.rawValue == wireName
        }?.descriptor.source
    }

    #if DEBUG
    internal static func resetForTests() {
        adapters.removeAll()
    }
    #endif
}
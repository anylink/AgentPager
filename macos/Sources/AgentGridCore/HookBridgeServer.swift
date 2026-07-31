import Foundation
import Network

/// PR3 起：HookBridgeServer 完全 Agent 无关，只负责"按行接收 + source 路由"。
/// 收到的 JSON line 先解析 source 字段，从 `AgentRegistry` 找到对应
/// `AgentAdapter`，由 adapter.reduceHook 归约成内部信号。
///
/// 当前仅 Codex 路径有完整实现（通过 `.codex(CodexHookPayload)` 信号包回
/// CodexHookPayload，再走现有 _pending 权限通道）。
/// 其他 agent 路径在 PR4+（ClaudeCodeAgentAdapter / CodebuddyAgentAdapter / ...）
/// 完成后增量启用。
public final class HookBridgeServer: CodexPermissionResolving, @unchecked Sendable {
    public typealias EventHandler = @Sendable (HookEnvelope) -> Void

    private let queue = DispatchQueue(label: "com.agentgrid.hook-server")
    private let lock = NSLock()
    private var listener: NWListener?
    private var pending: [String: NWConnection] = [:]
    private let eventHandler: EventHandler

    public init(eventHandler: @escaping EventHandler) {
        self.eventHandler = eventHandler
    }

    public func start(port: UInt16 = 49_361) throws {
        let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        listener.start(queue: queue)
        self.listener = listener
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        lock.withLock {
            pending.values.forEach { $0.cancel() }
            pending.removeAll()
        }
    }

    public func resolve(sessionID: String, decision: CodexPermissionDecision) {
        let connection = lock.withLock { pending.removeValue(forKey: sessionID) }
        guard let connection,
              let response = try? CodexHookOutput.permission(decision) else {
            return
        }
        connection.send(content: response, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }

    private func accept(_ connection: NWConnection) {
        connection.stateUpdateHandler = { state in
            if case let .failed(error) = state {
                fputs("AgentPager Hook 连接错误：\(error)\n", stderr)
                connection.cancel()
            }
        }
        connection.start(queue: queue)
        receive(on: connection, buffer: Data())
    }

    private func receive(on connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1_024) {
            [weak self] data, _, isComplete, error in
            guard let self else { return }
            var nextBuffer = buffer
            if let data {
                nextBuffer.append(data)
            }

            if let newline = nextBuffer.firstIndex(of: UInt8(ascii: "\n")) {
                let payloadData = nextBuffer.prefix(upTo: newline)
                self.handle(Data(payloadData), connection: connection)
                return
            }

            if isComplete || error != nil || nextBuffer.count > 1_048_576 {
                connection.cancel()
                return
            }
            self.receive(on: connection, buffer: nextBuffer)
        }
    }

    private func handle(_ data: Data, connection: NWConnection) {
        // 1. 解析为 dict，提取 source 字段
        guard let dict = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let source = AgentRegistryBridge.extractSource(from: dict),
              let adapter = AgentRegistry.get(source) else {
            connection.cancel()
            return
        }

        // 2. 构造 HookEnvelope（保留原始 dict 给 adapter 自解析）
        let envelope = AgentRegistryBridge.makeEnvelope(
            source: source, dict: dict
        )

        // 3. Codex 路径：保留 _pending permission 决策通道（避免客户端超时）
        if source == .codexCLI || source == .codexDesktop {
            let hookEventName = dict["hook_event_name"] as? String ?? ""
            let sessionId = dict["session_id"] as? String ?? ""
            if hookEventName == "PermissionRequest" && !sessionId.isEmpty {
                lock.withLock {
                    if let previous = pending[sessionId] {
                        previous.cancel()
                    }
                    pending[sessionId] = connection
                }
                eventHandler(envelope)
                return
            }

            eventHandler(envelope)
            connection.send(content: Data("\n".utf8), completion: .contentProcessed { _ in
                connection.cancel()
            })
            return
        }

        // 4. 非 Codex agent：路由但不持有 connection（暂未实现权限通道）
        eventHandler(envelope)
        connection.cancel()
    }
}

/// macOS 端 bridge helper：把 dict 转换为 HookEnvelope，
/// 集中 source 解析逻辑，避免 HookBridgeServer 与 CodexAdapter 重复实现。
enum AgentRegistryBridge {
    /// 从 dict 中提取 source 字段：
    /// - 存在 source 字段：按 wire name 解析为 AgentSource，找不到返回 nil
    /// - 缺失 source 字段：历史兼容视为 CodexCLI
    static func extractSource(from dict: [String: Any]) -> AgentSource? {
        guard let wireName = dict["source"] as? String else {
            return .codexCLI  // 历史兼容：Codex 当前不发送 source 字段
        }
        return AgentRegistry.resolveSource(wireName: wireName)
    }

    /// 把 dict 转成 HookEnvelope。
    static func makeEnvelope(source: AgentSource, dict: [String: Any]) -> HookEnvelope {
        let rawData = (try? JSONSerialization.data(withJSONObject: dict)) ?? Data()
        let raw = (try? JSONSerialization.jsonObject(with: rawData)) as? [String: Any] ?? dict
        return HookEnvelope(
            source: source,
            hookEventName: dict["hook_event_name"] as? String ?? "",
            sessionId: dict["session_id"] as? String ?? "",
            cwd: dict["cwd"] as? String ?? "",
            raw: raw,
            toolName: dict["tool_name"] as? String,
            toolUseId: dict["tool_use_id"] as? String,
            prompt: dict["prompt"] as? String,
            transcriptPath: dict["transcript_path"] as? String,
            model: dict["model"] as? String
        )
    }
}

private extension NSLock {
    func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try body()
    }
}
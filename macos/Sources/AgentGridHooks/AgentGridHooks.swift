import AgentGridCore
import Foundation
import Network

private final class HookClientState: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var didFinish = false
    private var response = Data()

    func append(_ data: Data) {
        lock.lock()
        response.append(data)
        lock.unlock()
    }

    func finish() {
        lock.lock()
        defer { lock.unlock() }
        guard !didFinish else { return }
        didFinish = true
        semaphore.signal()
    }

    func wait(until timeout: DispatchTime) -> Data {
        _ = semaphore.wait(timeout: timeout)
        lock.lock()
        defer { lock.unlock() }
        return response
    }
}

@main
struct AgentGridHooksCommand {
    static func main() {
        let input = FileHandle.standardInput.readDataToEndOfFile()
        guard let payload = try? JSONDecoder().decode(CodexHookPayload.self, from: input) else {
            // Hook 解析失败时保持 fail-open，不能阻塞 Codex。
            return
        }

        var mutableLine = input
        mutableLine.append(UInt8(ascii: "\n"))
        let line = mutableLine
        let connection = NWConnection(
            host: "127.0.0.1",
            port: NWEndpoint.Port(rawValue: 49_361)!,
            using: .tcp
        )
        let queue = DispatchQueue(label: "com.agentgrid.hook-client")
        let clientState = HookClientState()

        connection.stateUpdateHandler = { connectionState in
            switch connectionState {
            case .ready:
                connection.send(content: line, completion: .contentProcessed { error in
                    if error != nil {
                        clientState.finish()
                        return
                    }
                    connection.receiveMessage { data, _, _, _ in
                        if let data {
                            clientState.append(data)
                        }
                        clientState.finish()
                    }
                })
            case .failed, .cancelled:
                clientState.finish()
            default:
                break
            }
        }
        connection.start(queue: queue)

        let timeout: DispatchTime = payload.hookEventName == .permissionRequest
            ? .now() + 3_600
            : .now() + 5
        let response = clientState.wait(until: timeout)
        connection.cancel()

        if !response.isEmpty {
            FileHandle.standardOutput.write(response)
        }
    }
}

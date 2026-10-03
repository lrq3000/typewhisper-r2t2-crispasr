import Darwin
import Foundation
import os

final class ManagedRuntime: @unchecked Sendable {
    struct Endpoint: Sendable {
        let http: URL
        let realtime: URL
        let apiKey: String
    }
    private struct State {
        var enabled = true
        var process: Process?
        var configuration: String?
        var endpoint: Endpoint?
        var output = ""
    }
    private let lock = NSLock()
    private var state = State()
    private let directory: URL
    private let network = LocalNetwork.makeSession()
    private let logger = Logger(subsystem: "com.typewhisper.r2t2-crispasr", category: "Runtime")
    init(directory: URL) { self.directory = directory }

    func matches(model: URL, language: String?) -> Bool {
        lock.withLock { state.enabled && state.process?.isRunning == true && state.configuration == model.path + "\n" + (language ?? "auto") }
    }
    func endpoint() throws -> Endpoint {
        try lock.withLock {
            guard state.enabled, state.process?.isRunning == true, let endpoint = state.endpoint else { throw R2T2Error.message("R2T2 is not loaded.") }
            return endpoint
        }
    }

    func start(model: URL, language: String?) async throws {
        if matches(model: model, language: language) { return }
        stop()
        struct Metadata: Decodable { let revision: String; let loopbackOnly: Bool; let acceleration: String }
        let metadata = try JSONDecoder().decode(Metadata.self, from: Data(contentsOf: directory.appendingPathComponent("runtime.json")))
        guard metadata.loopbackOnly, metadata.revision == "340d7085eaa53c40a46dcb73a6d3d0448a480006" else {
            throw R2T2Error.message("The plugin requires its pinned loopback-only CrispASR runtime.")
        }
        for attempt in 0..<3 {
            do {
                try Task.checkCancellation()
                let port = try Self.freePort()
                let rawPort = try Self.adjacentPorts()
                let apiKey = UUID().uuidString + UUID().uuidString
                let endpoint = Endpoint(http: URL(string: "http://127.0.0.1:\(port)")!,
                    realtime: URL(string: "ws://127.0.0.1:\(rawPort + 1)/v1/realtime")!, apiKey: apiKey)
                let process = Process()
                process.executableURL = directory.appendingPathComponent("r2t2-watchdog")
                process.currentDirectoryURL = directory
                process.arguments = [String(getpid()), directory.appendingPathComponent("crispasr").path,
                    "--server", "--backend", "qwen3", "--model", model.path,
                    "--host", "127.0.0.1", "--port", String(port), "--ws-port", String(rawPort),
                    "--language", language ?? "auto", "--threads", String(max(1, min(8, ProcessInfo.processInfo.activeProcessorCount - 2)))]
                if metadata.acceleration == "metal" { process.arguments! += ["--gpu-backend", "metal"] }
                else { process.arguments! += ["--no-gpu"] }
                var environment = ProcessInfo.processInfo.environment
                environment["CRISPASR_API_KEYS"] = apiKey
                environment["CRISPASR_QWEN3_STREAM"] = "1"
                environment["CRISPASR_QWEN3_STREAM_STEP_MS"] = "320"
                environment["CRISPASR_CACHE_DIR"] = model.deletingLastPathComponent().path
                process.environment = environment
                let output = Pipe()
                process.standardOutput = output
                process.standardError = output
                output.fileHandleForReading.readabilityHandler = { [weak self] handle in
                    let data = handle.availableData
                    if !data.isEmpty { self?.capture(String(decoding: data, as: UTF8.self)) }
                }
                try lock.withLock {
                    guard state.enabled else { throw R2T2Error.message("Plugin is inactive.") }
                    state.output = ""
                    try process.run()
                    state.process = process
                    state.endpoint = endpoint
                }
                let deadline = Date().addingTimeInterval(300)
                while true {
                    try Task.checkCancellation()
                    guard process.isRunning, lock.withLock({ state.enabled }) else { throw R2T2Error.message("CrispASR exited during startup. \(outputTail)") }
                    guard Date() < deadline else { throw R2T2Error.message("CrispASR startup timed out. \(outputTail)") }
                    var request = URLRequest(url: endpoint.http.appendingPathComponent("health"))
                    request.timeoutInterval = 2
                    if let (_, response) = try? await network.data(for: request), (response as? HTTPURLResponse)?.statusCode == 200 { break }
                    try await Task.sleep(for: .milliseconds(100))
                }
                lock.withLock { state.configuration = model.path + "\n" + (language ?? "auto") }
                logger.info("CrispASR is ready for local R2T2 transcription")
                return
            } catch {
                stop()
                let output = outputTail.lowercased()
                guard attempt < 2, !Task.isCancelled, output.contains("bind") || output.contains("address already in use") else { throw error }
            }
        }
    }
    private var outputTail: String { lock.withLock { state.output } }
    private func capture(_ text: String) {
        lock.withLock {
            state.output.append(text)
            if state.output.utf8.count > 12000 { state.output = String(state.output.suffix(6000)) }
        }
    }

    func shutdown() { lock.withLock { state.enabled = false }; stop(); network.invalidateAndCancel() }
    func stop() {
        let process = lock.withLock { () -> Process? in
            let process = state.process
            state.process = nil; state.endpoint = nil; state.configuration = nil
            return process
        }
        guard let process else { return }
        (process.standardOutput as? Pipe)?.fileHandleForReading.readabilityHandler = nil
        guard process.isRunning else { return }
        process.terminate() // The watchdog forwards TERM, waits, then kills the group.
        let deadline = Date().addingTimeInterval(10)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        if process.isRunning { kill(process.processIdentifier, SIGKILL) }
    }

    private static func socketAt(_ port: UInt16) throws -> (Int32, UInt16) {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw R2T2Error.message("Cannot create a loopback socket.") }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0 else { close(descriptor); throw R2T2Error.message("Cannot reserve a loopback port.") }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let queried = withUnsafeMutablePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) }
        }
        guard queried == 0 else { close(descriptor); throw R2T2Error.message("Cannot query a loopback port.") }
        return (descriptor, UInt16(bigEndian: address.sin_port))
    }
    private static func freePort() throws -> UInt16 { let (descriptor, port) = try socketAt(0); close(descriptor); return port }
    private static func adjacentPorts() throws -> UInt16 {
        for _ in 0..<30 {
            let (first, port) = try socketAt(0)
            defer { close(first) }
            if port == UInt16.max { continue }
            if let (second, _) = try? socketAt(port + 1) { close(second); return port }
        }
        throw R2T2Error.message("Cannot reserve adjacent loopback ports.")
    }
}

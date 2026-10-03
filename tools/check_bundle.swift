// Native integration smoke test against the minimum host's actual framework.
// No recording, model download, user profile, or TypeWhisper UI is required.
import Foundation
import TypeWhisperPluginSDK

private final class SmokeEvents: EventBusProtocol, @unchecked Sendable {
    func subscribe(handler: @escaping @Sendable (TypeWhisperEvent) async -> Void) -> UUID { UUID() }
    func unsubscribe(id: UUID) {}
}

private final class SmokeHost: HostServices, @unchecked Sendable {
    let pluginDataDirectory: URL
    let eventBus: any EventBusProtocol = SmokeEvents()
    let availableRuleNames: [String] = []
    let activeAppBundleId: String? = nil
    let activeAppName: String? = nil
    private let lock = NSLock()
    private var preferences: [String: Any] = [:]
    init(directory: URL) { pluginDataDirectory = directory }
    func storeSecret(key: String, value: String) throws {}
    func loadSecret(key: String) -> String? { nil }
    func userDefault(forKey key: String) -> Any? { lock.withLock { preferences[key] } }
    func setUserDefault(_ value: Any?, forKey key: String) { lock.withLock { preferences[key] = value } }
    func notifyCapabilitiesChanged() {}
    func setStreamingDisplayActive(_ active: Bool) {}
}

@main
struct BundleCheck {
    @MainActor
    static func main() throws {
        guard CommandLine.arguments.count == 3 else { throw NSError(domain: "BundleCheck", code: 1) }
        let url = URL(fileURLWithPath: CommandLine.arguments[1])
        let dataDirectory = URL(fileURLWithPath: CommandLine.arguments[2])
        guard let bundle = Bundle(url: url) else { throw NSError(domain: "BundleCheck", code: 2) }
        try bundle.loadAndReturnError()
        guard let principal = bundle.principalClass as? any TypeWhisperPlugin.Type else {
            throw NSError(domain: "BundleCheck", code: 3, userInfo: [NSLocalizedDescriptionKey: "Principal class does not conform to the host's SDK"])
        }
        let plugin = principal.init()
        let host = SmokeHost(directory: dataDirectory)
        plugin.activate(host: host)
        defer { plugin.deactivate() }
        guard let engine = plugin as? any TranscriptionEnginePlugin,
              engine.providerId == "r2t2-crispasr", engine.supportedLanguages.count == 30,
              Set(engine.transcriptionModels.map(\.id)) == ["r2t2-q4_k", "r2t2-q8_0"],
              !engine.isConfigured, engine.supportsStreaming, !engine.supportsTranslation,
              plugin.settingsView != nil else {
            throw NSError(domain: "BundleCheck", code: 4, userInfo: [NSLocalizedDescriptionKey: "Plugin activation or bundle resource discovery failed"])
        }
        engine.selectModel("r2t2-q4_k")
        guard engine.selectedModelId == "r2t2-q4_k", host.userDefault(forKey: "selectedModel") as? String == "r2t2-q4_k" else {
            throw NSError(domain: "BundleCheck", code: 5)
        }
        print("Native bundle load, principal class, SDK identity, activation, settings, model selection and deactivation PASS")
    }
}

import Foundation
import SwiftUI
import TypeWhisperPluginSDK

@objc(R2T2CrispASRPlugin)
final class R2T2CrispASRPlugin: NSObject, LiveTranscriptionCapablePlugin,
    LiveTranscriptionProgressModeProviding, TranscriptionModelCatalogProviding,
    PluginDownloadedModelManaging, TranscriptPreviewFallbackPolicyProviding, DictionaryTermsCapabilityProviding, @unchecked Sendable {
    static let pluginId = "com.typewhisper.r2t2-crispasr"
    static let pluginName = "R2T2 (CrispASR)"
    private struct State {
        var host: (any HostServices)?
        var store: ModelStore?
        var runtime: ManagedRuntime?
        var engine: PluginEngine?
        var selected = "r2t2-q8_0"
        var setupError: String?
    }
    private let lock = NSLock()
    private var state = State()
    override init() { super.init() }
    let providerId = "r2t2-crispasr"
    let providerDisplayName = "R2T2 (CrispASR)"
    let supportsTranslation = false
    let supportsStreaming = true
    let allowsTranscriptPreviewFallback = false
    var dictionaryTermsSupport: DictionaryTermsSupport { .unsupported }
    var liveTranscriptionProgressMode: LiveTranscriptionProgressMode { .completeSnapshot }
    var selectedModelId: String? { lock.withLock { state.selected } }
    var supportedLanguages: [String] { lock.withLock { state.store?.catalog.languages ?? [] } }
    var setupError: String? { lock.withLock { state.setupError } }
    var isConfigured: Bool { lock.withLock { state.host != nil && state.store?.isDownloaded(state.selected) == true } }
    var transcriptionModels: [PluginModelInfo] { availableModels }
    var availableModels: [PluginModelInfo] {
        lock.withLock {
            guard let store = state.store else { return [] }
            return store.catalog.models.map { model in
                PluginModelInfo(id: model.id, displayName: model.name,
                    sizeDescription: String(format: "%.2f GB", Double(model.size) / 1_000_000_000),
                    languageCount: store.catalog.languages.count, downloaded: store.isDownloaded(model.id))
            }
        }
    }
    var downloadedModels: [PluginModelInfo] { availableModels.filter { $0.downloaded == true } }

    func activate(host: any HostServices) {
        let resources = Bundle(for: Self.self).resourceURL
        do {
            guard let resources else { throw R2T2Error.message("Plugin bundle resources are missing.") }
            let store = try ModelStore(root: host.pluginDataDirectory.appendingPathComponent("models"), catalogURL: resources.appendingPathComponent("models.json"))
            let runtime = ManagedRuntime(directory: resources.appendingPathComponent("Runtime"))
            let saved = host.userDefault(forKey: "selectedModel") as? String
            lock.withLock {
                state = State(host: host, store: store, runtime: runtime, engine: PluginEngine(store: store, runtime: runtime),
                    selected: store.catalog.models.contains { $0.id == saved } ? saved! : "r2t2-q8_0")
            }
        } catch {
            lock.withLock { state.host = host; state.setupError = error.localizedDescription }
        }
    }
    func deactivate() {
        let runtime = lock.withLock { () -> ManagedRuntime? in
            let runtime = state.runtime
            state.store?.shutdown()
            state.host = nil; state.engine = nil; state.runtime = nil; state.store = nil
            return runtime
        }
        runtime?.shutdown()
    }
    func selectModel(_ modelId: String) {
        let host = lock.withLock { () -> (any HostServices)? in
            guard state.store?.catalog.models.contains(where: { $0.id == modelId }) == true else { return nil }
            state.host?.setUserDefault(modelId, forKey: "selectedModel")
            state.selected = modelId
            return state.host
        }
        host?.notifyCapabilitiesChanged()
    }
    private struct Context: Sendable {
        let engine: PluginEngine
        let id: String
        let host: any HostServices
    }
    private func context(language: String?, translate: Bool) throws -> (Context, String?) {
        guard !translate else { throw R2T2Error.message("R2T2 does not provide audio translation.") }
        return try lock.withLock {
            guard let engine = state.engine, let host = state.host else { throw R2T2Error.message(state.setupError ?? "Plugin is inactive.") }
            let hint = language == "auto" || language?.isEmpty != false ? nil : language?.lowercased()
            if let hint, state.store?.catalog.languages.contains(hint) != true { throw R2T2Error.message("Unsupported R2T2 language.") }
            // Pass canonical names because CrispASR's ISO map omits several of
            // the model's languages, including Cantonese, Finnish and Filipino.
            let name = hint.flatMap { state.store?.catalog.languageNames[$0] }
            return (Context(engine: engine, id: state.selected, host: host), name)
        }
    }
    func transcribe(audio: AudioData, language: String?, translate: Bool, prompt: String?) async throws -> PluginTranscriptionResult {
        let (context, hint) = try context(language: language, translate: translate)
        return try await context.engine.batch(id: context.id, audio: audio, language: hint, unload: context.host.unloadsModelsImmediatelyAfterUse)
    }
    func transcribe(audio: AudioData, language: String?, translate: Bool, prompt: String?,
                    onProgress: @escaping @Sendable (String) -> Bool) async throws -> PluginTranscriptionResult {
        let session = try await createLiveTranscriptionSession(language: language, translate: translate, prompt: prompt, onProgress: onProgress)
        do { try await session.appendAudio(samples: audio.samples); return try await session.finish() }
        catch { await session.cancel(); throw error }
    }
    func createLiveTranscriptionSession(language: String?, translate: Bool, prompt: String?,
                                       onProgress: @escaping @Sendable (String) -> Bool) async throws -> any LiveTranscriptionSession {
        let (context, hint) = try context(language: language, translate: translate)
        return try await context.engine.live(id: context.id, language: hint, unload: context.host.unloadsModelsImmediatelyAfterUse, onProgress: onProgress)
    }
    func downloadModel(_ id: String, progress: @escaping @Sendable (Double) -> Void) async throws {
        let (context, _) = try context(language: nil, translate: false)
        try await context.engine.download(id, progress: progress)
        context.host.notifyCapabilitiesChanged()
    }
    func deleteDownloadedModel(_ modelId: String) async throws {
        let (context, _) = try context(language: nil, translate: false)
        try await context.engine.remove(modelId)
        context.host.notifyCapabilitiesChanged()
    }
    func unload() async throws {
        let (context, _) = try context(language: nil, translate: false)
        try await context.engine.unload()
    }
    @MainActor var settingsView: AnyView? { AnyView(PluginSettingsView(plugin: self)) }
}

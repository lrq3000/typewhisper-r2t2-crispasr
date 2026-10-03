import SwiftUI
import TypeWhisperPluginSDK

@MainActor
private final class SettingsModel: ObservableObject {
    let plugin: R2T2CrispASRPlugin
    @Published var models: [PluginModelInfo] = []
    @Published var status = ""
    @Published var progress: Double = 0
    @Published var busy = false
    @Published var selected: String?
    private var task: Task<Void, Never>?
    init(plugin: R2T2CrispASRPlugin) { self.plugin = plugin; refresh() }
    func refresh() { models = plugin.availableModels; selected = plugin.selectedModelId; if let error = plugin.setupError { status = error } }
    func select(_ id: String) { plugin.selectModel(id); refresh() }
    private func perform(_ action: @escaping @Sendable () async throws -> Void) {
        guard !busy else { return }
        busy = true; status = "Working…"; progress = 0
        task = Task {
            defer { busy = false; task = nil; refresh() }
            do { try await action(); status = "Ready" }
            catch is CancellationError { status = "Canceled" }
            catch { status = error.localizedDescription }
        }
    }
    func download(_ id: String) {
        perform { [plugin, weak self] in
            try await plugin.downloadModel(id) { [weak self] value in
                Task { @MainActor [weak self] in self?.progress = value }
            }
        }
    }
    func remove(_ id: String) { perform { [plugin] in try await plugin.deleteDownloadedModel(id) } }
    func unload() { perform { [plugin] in try await plugin.unload() } }
    func cancel() { task?.cancel() }
}

@MainActor
struct PluginSettingsView: View {
    @StateObject private var model: SettingsModel
    init(plugin: R2T2CrispASRPlugin) { _model = StateObject(wrappedValue: SettingsModel(plugin: plugin)) }
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("R2T2 · CrispASR").font(.headline)
            Text("On-device transcription. Download a model, then choose it for dictation.").foregroundStyle(.secondary)
            ForEach(model.models, id: \.id) { item in
                HStack {
                    VStack(alignment: .leading) { Text(item.displayName); Text(item.sizeDescription).font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    if item.downloaded == true {
                        Button(model.selected == item.id ? "Selected" : "Use model") { model.select(item.id) }
                            .disabled(model.busy || model.selected == item.id)
                        Button("Remove") { model.remove(item.id) }.disabled(model.busy)
                    } else {
                        Button("Download") { model.download(item.id) }.disabled(model.busy)
                    }
                }
            }
            if model.busy { ProgressView(value: model.progress); Button("Cancel") { model.cancel() } }
            Text(model.status).font(.caption).textSelection(.enabled)
            Button("Unload model") { model.unload() }.disabled(model.busy)
            Text("Weights use the NetEase Youdao Model Use License. MODEL_LICENSE is retained with each download.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding().frame(minWidth: 440)
    }
}

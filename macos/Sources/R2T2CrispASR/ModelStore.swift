import CryptoKit
import Darwin
import Foundation

struct CatalogModel: Codable, Sendable, Identifiable {
    let id: String
    let name: String
    let file: String
    let size: Int64
    let sha256: String
}
struct ModelCatalog: Codable, Sendable {
    let repository: String
    let revision: String
    let licenseFile: String
    let languages: [String]
    let languageNames: [String: String]
    let models: [CatalogModel]
}

final class ModelStore: @unchecked Sendable {
    let catalog: ModelCatalog
    private let root: URL
    private let models: [String: CatalogModel]
    private let lock = NSLock()
    private var disabled = false
    private var activeDownload: URLSession?
    private struct Verification: Codable {
        let size: Int64
        let modified: Double
        let sha256: String
    }

    init(root: URL, catalogURL: URL) throws {
        self.root = root
        catalog = try JSONDecoder().decode(ModelCatalog.self, from: Data(contentsOf: catalogURL))
        guard Set(catalog.models.map(\.id)).count == catalog.models.count else { throw R2T2Error.message("Duplicate model IDs.") }
        for model in catalog.models {
            guard !model.id.contains("/"), !model.id.contains("\\"), model.id != ".", model.id != "..",
                  URL(fileURLWithPath: model.file).lastPathComponent == model.file else { throw R2T2Error.message("Invalid model catalogue path.") }
        }
        models = Dictionary(uniqueKeysWithValues: catalog.models.map { ($0.id, $0) })
    }
    func model(_ id: String) throws -> CatalogModel {
        guard let model = models[id] else { throw R2T2Error.message("Unknown R2T2 model.") }
        return model
    }
    func modelURL(_ id: String) throws -> URL { root.appendingPathComponent(try model(id).id).appendingPathComponent(try model(id).file) }
    private func licenseURL(_ id: String) -> URL { root.appendingPathComponent(id).appendingPathComponent("MODEL_LICENSE") }
    private func stampURL(_ id: String) throws -> URL { try modelURL(id).appendingPathExtension("verified.json") }

    func isDownloaded(_ id: String) -> Bool {
        do {
            let model = try model(id)
            let attributes = try FileManager.default.attributesOfItem(atPath: modelURL(id).path)
            let stamp = try JSONDecoder().decode(Verification.self, from: Data(contentsOf: stampURL(id)))
            return (attributes[.size] as? NSNumber)?.int64Value == model.size && stamp.size == model.size
                && (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 == stamp.modified
                && stamp.sha256 == model.sha256 && FileManager.default.fileExists(atPath: licenseURL(id).path)
        } catch { return false }
    }

    private func checksum(_ url: URL) throws -> String {
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        var hash = SHA256()
        while let bytes = try input.read(upToCount: 131072), !bytes.isEmpty {
            try Task.checkCancellation()
            guard !lock.withLock({ disabled }) else { throw CancellationError() }
            hash.update(data: bytes)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
    func verifyForLoad(_ id: String) async throws {
        guard isDownloaded(id), try checksum(modelURL(id)) == model(id).sha256 else {
            throw R2T2Error.message("Download or repair the selected R2T2 model first.")
        }
    }

    func download(_ id: String, progress: @escaping @Sendable (Double) -> Void) async throws {
        let model = try model(id)
        let directory = root.appendingPathComponent(model.id)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let disk = try FileManager.default.attributesOfFileSystem(forPath: directory.path)
        guard ((disk[.systemFreeSize] as? NSNumber)?.int64Value ?? 0) > model.size + 64 * 1024 * 1024 else {
            throw R2T2Error.message("Insufficient free disk space for this model.")
        }
        let weightPart = directory.appendingPathComponent(UUID().uuidString + ".part")
        let licensePart = directory.appendingPathComponent(UUID().uuidString + ".part")
        defer { try? FileManager.default.removeItem(at: weightPart); try? FileManager.default.removeItem(at: licensePart) }
        let base = "https://huggingface.co/\(catalog.repository)/resolve/\(catalog.revision)/"
        try await downloadFile(URL(string: base + catalog.licenseFile)!, to: licensePart, maximum: 65536, progress: { _ in })
        try await downloadFile(URL(string: base + model.file)!, to: weightPart, maximum: model.size, progress: progress)
        let attributes = try FileManager.default.attributesOfItem(atPath: weightPart.path)
        guard (attributes[.size] as? NSNumber)?.int64Value == model.size, try checksum(weightPart) == model.sha256 else {
            throw R2T2Error.message("Model size or SHA-256 verification failed.")
        }
        try Task.checkCancellation()
        // rename() atomically replaces on the same filesystem. Removing the old
        // file before moving would lose a working model after a failed download.
        try promote(licensePart, to: licenseURL(id))
        try promote(weightPart, to: modelURL(id))
        let installed = try FileManager.default.attributesOfItem(atPath: modelURL(id).path)
        let stamp = Verification(size: model.size, modified: (installed[.modificationDate] as! Date).timeIntervalSince1970, sha256: model.sha256)
        try JSONEncoder().encode(stamp).write(to: stampURL(id), options: .atomic)
        progress(1)
    }

    private func promote(_ source: URL, to target: URL) throws {
        guard rename(source.path, target.path) == 0 else { throw R2T2Error.message("Could not install verified model: \(String(cString: strerror(errno)))") }
    }
    private func downloadFile(_ url: URL, to destination: URL, maximum: Int64,
                              progress: @escaping @Sendable (Double) -> Void) async throws {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForResource = 7200
        let session = URLSession(configuration: config)
        try lock.withLock {
            guard !disabled else { session.invalidateAndCancel(); throw CancellationError() }
            activeDownload = session
        }
        defer { lock.withLock { if activeDownload === session { activeDownload = nil } }; session.invalidateAndCancel() }
        let delegate = DownloadProgress(maximum: maximum, progress: progress)
        let (temporary, response) = try await session.download(from: url, delegate: delegate)
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let http = response as? HTTPURLResponse, http.statusCode == 200, response.url?.scheme == "https" else {
            throw R2T2Error.message("Model download failed or redirected to an insecure URL.")
        }
        let size = (try FileManager.default.attributesOfItem(atPath: temporary.path)[.size] as? NSNumber)?.int64Value ?? 0
        guard size > 0 && size <= maximum else { throw R2T2Error.message("Invalid download size.") }
        try FileManager.default.copyItem(at: temporary, to: destination)
    }
    func remove(_ id: String) throws {
        guard !lock.withLock({ disabled }) else { throw CancellationError() }
        let directory = root.appendingPathComponent(try model(id).id)
        if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
    }
    func shutdown() {
        let session = lock.withLock { () -> URLSession? in disabled = true; return activeDownload }
        session?.invalidateAndCancel()
    }
}

private final class DownloadProgress: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    let maximum: Int64
    let progress: @Sendable (Double) -> Void
    init(maximum: Int64, progress: @escaping @Sendable (Double) -> Void) { self.maximum = maximum; self.progress = progress }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        if totalBytesWritten > maximum { downloadTask.cancel() }
        progress(min(0.99, Double(totalBytesWritten) / Double(maximum)))
    }
}

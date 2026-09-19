import Foundation

enum SharedPayloadKind: String, Codable {
    case text
    case image
}

struct SharedPayloadEnvelope: Codable {
    let id: UUID
    let kind: SharedPayloadKind
    let text: String?
    let fileName: String?
    let createdAt: Date
}

enum SharedPayloadStore {
    static let appGroup = "group.universe.dia.conphone"
    private static let defaultsKey = "pending-share-v2"
    private static let directoryName = "SharedPayloads"
    private static let maxTextBytes = 1024 * 1024
    private static let maxImageBytes = 32 * 1024 * 1024
    private static let maxQueuedItems = 8
    private static let maxQueuedImageBytes = 40 * 1024 * 1024

    static func saveText(_ text: String) throws {
        let bytes = Data(text.utf8)
        guard !bytes.isEmpty, bytes.count <= maxTextBytes else {
            throw StoreError.tooLarge
        }

        try saveEnvelope(SharedPayloadEnvelope(
            id: UUID(),
            kind: .text,
            text: text,
            fileName: nil,
            createdAt: Date()
        ))
    }

    static func saveImageFile(_ source: URL) throws {
        let values = try source.resourceValues(forKeys: [.fileSizeKey])
        guard let size = values.fileSize, size > 0, size <= maxImageBytes else {
            throw StoreError.tooLarge
        }

        let directory = try sharedDirectory()
        cleanupStaleFiles(in: directory)

        let fileName = "share-\(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try FileManager.default.copyItem(at: source, to: destination)

        do {
            try saveEnvelope(SharedPayloadEnvelope(
                id: UUID(),
                kind: .image,
                text: nil,
                fileName: fileName,
                createdAt: Date()
            ))
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
    }

    static func saveImageData(_ data: Data) throws {
        guard !data.isEmpty, data.count <= maxImageBytes else {
            throw StoreError.tooLarge
        }

        let directory = try sharedDirectory()
        cleanupStaleFiles(in: directory)

        let fileName = "share-\(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try data.write(to: destination, options: .atomic)

        do {
            try saveEnvelope(SharedPayloadEnvelope(
                id: UUID(),
                kind: .image,
                text: nil,
                fileName: fileName,
                createdAt: Date()
            ))
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
    }

    static func peek() -> (SharedPayloadEnvelope, Data?)? {
        var queue = loadQueue()
        pruneExpired(&queue)
        saveQueue(queue)

        guard let envelope = queue.first else { return nil }

        switch envelope.kind {
        case .text:
            return (envelope, nil)

        case .image:
            guard let name = envelope.fileName,
                  let directory = try? sharedDirectory(),
                  let file = safeFileURL(name, in: directory),
                  let imageData = try? Data(contentsOf: file),
                  !imageData.isEmpty,
                  imageData.count <= maxImageBytes else {
                clear(envelope.id)
                return peek()
            }

            return (envelope, imageData)
        }
    }

    static func clear(_ id: UUID) {
        var queue = loadQueue()
        guard let index = queue.firstIndex(where: { $0.id == id }) else {
            return
        }

        let envelope = queue.remove(at: index)
        saveQueue(queue)

        if let name = envelope.fileName,
           let directory = try? sharedDirectory(),
           let file = safeFileURL(name, in: directory) {
            try? FileManager.default.removeItem(at: file)
        }
    }

    private static func saveEnvelope(_ envelope: SharedPayloadEnvelope) throws {
        guard UserDefaults(suiteName: appGroup) != nil else {
            throw StoreError.appGroupUnavailable
        }

        var queue = loadQueue()
        pruneExpired(&queue)

        guard queue.count < maxQueuedItems else {
            throw StoreError.queueFull
        }

        if envelope.kind == .image {
            let currentBytes = queuedImageBytes(queue)
            let newBytes = imageSize(envelope)
            guard newBytes > 0,
                  currentBytes + newBytes <= maxQueuedImageBytes else {
                throw StoreError.queueFull
            }
        }

        queue.append(envelope)
        saveQueue(queue)
    }

    private static func loadQueue() -> [SharedPayloadEnvelope] {
        guard let defaults = UserDefaults(suiteName: appGroup),
              let data = defaults.data(forKey: defaultsKey),
              let queue = try? JSONDecoder().decode(
                [SharedPayloadEnvelope].self,
                from: data) else {
            return []
        }
        return queue
    }

    private static func saveQueue(_ queue: [SharedPayloadEnvelope]) {
        guard let defaults = UserDefaults(suiteName: appGroup) else { return }

        if queue.isEmpty {
            defaults.removeObject(forKey: defaultsKey)
            return
        }

        if let data = try? JSONEncoder().encode(queue) {
            defaults.set(data, forKey: defaultsKey)
        }
    }

    private static func pruneExpired(_ queue: inout [SharedPayloadEnvelope]) {
        let cutoff = Date().addingTimeInterval(-24 * 60 * 60)
        let expired = queue.filter { $0.createdAt < cutoff }
        queue.removeAll { $0.createdAt < cutoff }

        guard let directory = try? sharedDirectory() else { return }
        for envelope in expired {
            if let name = envelope.fileName,
               let file = safeFileURL(name, in: directory) {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }

    private static func queuedImageBytes(_ queue: [SharedPayloadEnvelope]) -> Int {
        queue.reduce(0) { partial, envelope in
            partial + imageSize(envelope)
        }
    }

    private static func imageSize(_ envelope: SharedPayloadEnvelope) -> Int {
        guard envelope.kind == .image,
              let name = envelope.fileName,
              let directory = try? sharedDirectory(),
              let file = safeFileURL(name, in: directory),
              let values = try? file.resourceValues(forKeys: [.fileSizeKey]) else {
            return 0
        }

        return values.fileSize ?? 0
    }

    private static func sharedDirectory() throws -> URL {
        guard let root = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroup) else {
            throw StoreError.appGroupUnavailable
        }

        let directory = root.appendingPathComponent(directoryName, isDirectory: true)
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true)
        return directory
    }

    private static func safeFileURL(
        _ fileName: String,
        in directory: URL
    ) -> URL? {
        guard !fileName.isEmpty,
              (fileName as NSString).lastPathComponent == fileName else {
            return nil
        }
        return directory.appendingPathComponent(fileName)
    }

    private static func cleanupStaleFiles(in directory: URL) {
        let cutoff = Date().addingTimeInterval(-24 * 60 * 60)
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]) else {
            return
        }

        for file in files {
            let modified = try? file.resourceValues(
                forKeys: [.contentModificationDateKey]).contentModificationDate
            if let modified, modified < cutoff {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }

    enum StoreError: LocalizedError {
        case tooLarge
        case appGroupUnavailable
        case queueFull

        var errorDescription: String? {
            switch self {
            case .tooLarge:
                return "공유 내용이 안전 크기 제한을 초과합니다."
            case .appGroupUnavailable:
                return "Con.Phone 공유 저장소를 사용할 수 없습니다."
            case .queueFull:
                return "PC 전송 대기열이 가득 찼습니다. Con.Phone 앱을 열어 먼저 전송해 주세요."
            }
        }
    }
}

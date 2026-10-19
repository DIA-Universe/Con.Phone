import Darwin
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

    private static let metadataFileName = "queue-v3.json"
    private static let lockFileName = ".queue-v3.lock"
    private static let legacyDefaultsKeyV2 = "pending-share-v2"
    private static let legacyDefaultsKeyV1 = "pending-share-v1"
    private static let directoryName = "SharedPayloads"

    private static let maxTextBytes = 1024 * 1024
    private static let maxImageBytes = 32 * 1024 * 1024
    private static let maxQueuedItems = 8
    private static let maxQueuedImageBytes = 40 * 1024 * 1024
    private static let maxAge: TimeInterval = 24 * 60 * 60

    static func saveText(_ text: String) throws {
        let bytes = Data(text.utf8)
        guard !bytes.isEmpty, bytes.count <= maxTextBytes else {
            throw StoreError.tooLarge
        }

        let envelope = SharedPayloadEnvelope(
            id: UUID(),
            kind: .text,
            text: text,
            fileName: nil,
            createdAt: Date()
        )

        try withQueueLock { directory in
            var queue = loadQueueUnlocked(in: directory)
            pruneExpiredUnlocked(&queue, in: directory)
            try ensureCapacityUnlocked(queue, newEnvelope: envelope, directory: directory)
            queue.append(envelope)
            try saveQueueUnlocked(queue, in: directory)
        }
    }

    static func saveImageFile(_ source: URL) throws {
        let values = try source.resourceValues(forKeys: [.fileSizeKey])
        guard let size = values.fileSize,
              size > 0,
              size <= maxImageBytes else {
            throw StoreError.tooLarge
        }

        let directory = try sharedDirectory()
        let fileName = "share-\(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try FileManager.default.copyItem(at: source, to: destination)

        let copiedValues = try destination.resourceValues(
            forKeys: [.fileSizeKey])
        guard let copiedSize = copiedValues.fileSize,
              copiedSize > 0,
              copiedSize <= maxImageBytes else {
            try? FileManager.default.removeItem(at: destination)
            throw StoreError.tooLarge
        }

        let envelope = SharedPayloadEnvelope(
            id: UUID(),
            kind: .image,
            text: nil,
            fileName: fileName,
            createdAt: Date()
        )

        do {
            try withQueueLock { lockedDirectory in
                var queue = loadQueueUnlocked(in: lockedDirectory)
                pruneExpiredUnlocked(&queue, in: lockedDirectory)
                try ensureCapacityUnlocked(
                    queue,
                    newEnvelope: envelope,
                    directory: lockedDirectory)
                queue.append(envelope)
                try saveQueueUnlocked(queue, in: lockedDirectory)
                cleanupOrphanedFilesUnlocked(queue, in: lockedDirectory)
            }
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
        let fileName = "share-\(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try data.write(to: destination, options: .atomic)

        let envelope = SharedPayloadEnvelope(
            id: UUID(),
            kind: .image,
            text: nil,
            fileName: fileName,
            createdAt: Date()
        )

        do {
            try withQueueLock { lockedDirectory in
                var queue = loadQueueUnlocked(in: lockedDirectory)
                pruneExpiredUnlocked(&queue, in: lockedDirectory)
                try ensureCapacityUnlocked(
                    queue,
                    newEnvelope: envelope,
                    directory: lockedDirectory)
                queue.append(envelope)
                try saveQueueUnlocked(queue, in: lockedDirectory)
                cleanupOrphanedFilesUnlocked(queue, in: lockedDirectory)
            }
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
    }

    static func peek() -> (SharedPayloadEnvelope, Data?)? {
        try? withQueueLock { directory in
            var queue = loadQueueUnlocked(in: directory)
            pruneExpiredUnlocked(&queue, in: directory)

            while let envelope = queue.first {
                switch envelope.kind {
                case .text:
                    try saveQueueUnlocked(queue, in: directory)
                    return (envelope, nil)

                case .image:
                    guard let name = envelope.fileName,
                          let file = safeFileURL(name, in: directory),
                          let values = try? file.resourceValues(forKeys: [.fileSizeKey]),
                          let size = values.fileSize,
                          size > 0,
                          size <= maxImageBytes,
                          let data = try? Data(contentsOf: file, options: [.mappedIfSafe]),
                          !data.isEmpty,
                          data.count == size else {
                        queue.removeFirst()
                        if let name = envelope.fileName,
                           let file = safeFileURL(name, in: directory) {
                            try? FileManager.default.removeItem(at: file)
                        }
                        continue
                    }

                    try saveQueueUnlocked(queue, in: directory)
                    return (envelope, data)
                }
            }

            try saveQueueUnlocked(queue, in: directory)
            cleanupOrphanedFilesUnlocked(queue, in: directory)
            return nil
        }
    }

    static func clear(_ id: UUID) {
        try? withQueueLock { directory in
            var queue = loadQueueUnlocked(in: directory)
            pruneExpiredUnlocked(&queue, in: directory)

            guard let index = queue.firstIndex(where: { $0.id == id }) else {
                try saveQueueUnlocked(queue, in: directory)
                return
            }

            let envelope = queue.remove(at: index)
            try saveQueueUnlocked(queue, in: directory)

            if let name = envelope.fileName,
               let file = safeFileURL(name, in: directory) {
                try? FileManager.default.removeItem(at: file)
            }

            cleanupOrphanedFilesUnlocked(queue, in: directory)
        }
    }

    static func count() -> Int {
        (try? withQueueLock { directory in
            var queue = loadQueueUnlocked(in: directory)
            pruneExpiredUnlocked(&queue, in: directory)
            try saveQueueUnlocked(queue, in: directory)
            cleanupOrphanedFilesUnlocked(queue, in: directory)
            return queue.count
        }) ?? 0
    }

    private static func ensureCapacityUnlocked(
        _ queue: [SharedPayloadEnvelope],
        newEnvelope: SharedPayloadEnvelope,
        directory: URL
    ) throws {
        guard queue.count < maxQueuedItems else {
            throw StoreError.queueFull
        }

        if newEnvelope.kind == .image {
            let currentBytes = queuedImageBytesUnlocked(queue, in: directory)
            let newBytes = imageSizeUnlocked(newEnvelope, in: directory)

            guard newBytes > 0,
                  currentBytes + newBytes <= maxQueuedImageBytes else {
                throw StoreError.queueFull
            }
        }
    }

    private static func withQueueLock<T>(
        _ body: (URL) throws -> T
    ) throws -> T {
        let directory = try sharedDirectory()
        let lockURL = directory.appendingPathComponent(lockFileName)

        let descriptor = lockURL.path.withCString {
            Darwin.open(
                $0,
                O_CREAT | O_RDWR,
                S_IRUSR | S_IWUSR)
        }

        guard descriptor >= 0 else {
            throw StoreError.storageUnavailable
        }

        defer {
            _ = Darwin.flock(descriptor, LOCK_UN)
            _ = Darwin.close(descriptor)
        }

        guard Darwin.flock(descriptor, LOCK_EX) == 0 else {
            throw StoreError.storageUnavailable
        }

        return try body(directory)
    }

    private static func loadQueueUnlocked(
        in directory: URL
    ) -> [SharedPayloadEnvelope] {
        let metadata = directory.appendingPathComponent(metadataFileName)

        if let data = try? Data(contentsOf: metadata),
           let queue = try? JSONDecoder().decode(
                [SharedPayloadEnvelope].self,
                from: data) {
            return queue
        }

        let migrated = migrateLegacyQueueUnlocked()
        if !migrated.isEmpty {
            try? saveQueueUnlocked(migrated, in: directory)
        }
        return migrated
    }

    private static func migrateLegacyQueueUnlocked() -> [SharedPayloadEnvelope] {
        guard let defaults = UserDefaults(suiteName: appGroup) else {
            return []
        }

        var migrated: [SharedPayloadEnvelope] = []

        if let data = defaults.data(forKey: legacyDefaultsKeyV2),
           let queue = try? JSONDecoder().decode(
                [SharedPayloadEnvelope].self,
                from: data) {
            migrated = queue
        } else if let data = defaults.data(forKey: legacyDefaultsKeyV1),
                  let envelope = try? JSONDecoder().decode(
                    SharedPayloadEnvelope.self,
                    from: data) {
            migrated = [envelope]
        }

        defaults.removeObject(forKey: legacyDefaultsKeyV2)
        defaults.removeObject(forKey: legacyDefaultsKeyV1)
        return migrated
    }

    private static func saveQueueUnlocked(
        _ queue: [SharedPayloadEnvelope],
        in directory: URL
    ) throws {
        let metadata = directory.appendingPathComponent(metadataFileName)

        if queue.isEmpty {
            if FileManager.default.fileExists(atPath: metadata.path) {
                try FileManager.default.removeItem(at: metadata)
            }
            return
        }

        let data = try JSONEncoder().encode(queue)
        try data.write(to: metadata, options: .atomic)
    }

    private static func pruneExpiredUnlocked(
        _ queue: inout [SharedPayloadEnvelope],
        in directory: URL
    ) {
        let cutoff = Date().addingTimeInterval(-maxAge)
        let expired = queue.filter { $0.createdAt < cutoff }
        queue.removeAll { $0.createdAt < cutoff }

        for envelope in expired {
            if let name = envelope.fileName,
               let file = safeFileURL(name, in: directory) {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }

    private static func queuedImageBytesUnlocked(
        _ queue: [SharedPayloadEnvelope],
        in directory: URL
    ) -> Int {
        queue.reduce(0) { partial, envelope in
            partial + imageSizeUnlocked(envelope, in: directory)
        }
    }

    private static func imageSizeUnlocked(
        _ envelope: SharedPayloadEnvelope,
        in directory: URL
    ) -> Int {
        guard envelope.kind == .image,
              let name = envelope.fileName,
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

        let directory = root.appendingPathComponent(
            directoryName,
            isDirectory: true)

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

        let candidate = directory
            .appendingPathComponent(fileName)
            .standardizedFileURL

        guard candidate.deletingLastPathComponent() == directory.standardizedFileURL else {
            return nil
        }

        return candidate
    }

    private static func cleanupOrphanedFilesUnlocked(
        _ queue: [SharedPayloadEnvelope],
        in directory: URL
    ) {
        let referenced = Set(queue.compactMap(\.fileName))
        let cutoff = Date().addingTimeInterval(-maxAge)

        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]) else {
            return
        }

        for file in files {
            if file.lastPathComponent == metadataFileName ||
               file.lastPathComponent == lockFileName ||
               referenced.contains(file.lastPathComponent) {
                continue
            }

            let modified = try? file.resourceValues(
                forKeys: [.contentModificationDateKey]).contentModificationDate

            if modified == nil || modified! < cutoff {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }

    enum StoreError: LocalizedError {
        case tooLarge
        case appGroupUnavailable
        case queueFull
        case storageUnavailable

        var errorDescription: String? {
            switch self {
            case .tooLarge:
                return "공유 내용이 안전 크기 제한을 초과합니다."
            case .appGroupUnavailable:
                return "Con.Phone 공유 저장소를 사용할 수 없습니다."
            case .queueFull:
                return "PC 전송 대기열이 가득 찼습니다. Con.Phone 앱을 열어 먼저 전송해 주세요."
            case .storageUnavailable:
                return "Con.Phone 공유 대기열 잠금을 사용할 수 없습니다."
            }
        }
    }
}

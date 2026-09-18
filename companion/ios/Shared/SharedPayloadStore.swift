import Foundation

enum SharedPayloadKind: String, Codable {
    case text
    case image
}

struct SharedPayloadEnvelope: Codable {
    let kind: SharedPayloadKind
    let text: String?
    let fileName: String?
    let createdAt: Date
}

enum SharedPayloadStore {
    static let appGroup = "group.universe.dia.conphone"
    private static let defaultsKey = "pending-share-v1"
    private static let directoryName = "SharedPayloads"
    private static let maxTextBytes = 1024 * 1024
    private static let maxImageBytes = 40 * 1024 * 1024

    static func saveText(_ text: String) throws {
        let bytes = Data(text.utf8)
        guard !bytes.isEmpty, bytes.count <= maxTextBytes else {
            throw StoreError.tooLarge
        }

        try saveEnvelope(SharedPayloadEnvelope(
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

        let fileName = "share-(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try FileManager.default.copyItem(at: source, to: destination)

        try saveEnvelope(SharedPayloadEnvelope(
            kind: .image,
            text: nil,
            fileName: fileName,
            createdAt: Date()
        ))
    }

    static func saveImageData(_ data: Data) throws {
        guard !data.isEmpty, data.count <= maxImageBytes else {
            throw StoreError.tooLarge
        }

        let directory = try sharedDirectory()
        cleanupStaleFiles(in: directory)

        let fileName = "share-(UUID().uuidString).bin"
        let destination = directory.appendingPathComponent(fileName)
        try data.write(to: destination, options: .atomic)

        try saveEnvelope(SharedPayloadEnvelope(
            kind: .image,
            text: nil,
            fileName: fileName,
            createdAt: Date()
        ))
    }

    static func consume() -> (SharedPayloadEnvelope, Data?)? {
        guard let defaults = UserDefaults(suiteName: appGroup),
              let data = defaults.data(forKey: defaultsKey),
              let envelope = try? JSONDecoder().decode(
                SharedPayloadEnvelope.self,
                from: data) else {
            return nil
        }

        defaults.removeObject(forKey: defaultsKey)

        if Date().timeIntervalSince(envelope.createdAt) > 24 * 60 * 60 {
            if let name = envelope.fileName,
               let directory = try? sharedDirectory() {
                try? FileManager.default.removeItem(
                    at: directory.appendingPathComponent(name))
            }
            return nil
        }

        switch envelope.kind {
        case .text:
            return (envelope, nil)
        case .image:
            guard let name = envelope.fileName,
                  let directory = try? sharedDirectory() else {
                return nil
            }
            let file = directory.appendingPathComponent(name)
            defer { try? FileManager.default.removeItem(at: file) }
            guard let imageData = try? Data(contentsOf: file),
                  imageData.count <= maxImageBytes else {
                return nil
            }
            return (envelope, imageData)
        }
    }

    private static func saveEnvelope(_ envelope: SharedPayloadEnvelope) throws {
        guard let defaults = UserDefaults(suiteName: appGroup) else {
            throw StoreError.appGroupUnavailable
        }
        let data = try JSONEncoder().encode(envelope)
        defaults.set(data, forKey: defaultsKey)
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

    enum StoreError: Error {
        case tooLarge
        case appGroupUnavailable
    }
}

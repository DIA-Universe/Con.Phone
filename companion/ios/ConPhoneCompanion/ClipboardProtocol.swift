import Foundation
import CryptoKit
import Security

enum ClipboardKind: UInt8 {
    case text = 1
    case png = 2
    case jpeg = 3
    case webp = 4
}

struct ClipboardContent {
    let kind: ClipboardKind
    let data: Data

    var hash: Data {
        Data(SHA256.hash(data: data))
    }
}

struct ClipboardWireChunk {
    let transferId: Data
    let kind: ClipboardKind
    let index: Int
    let count: Int
    let totalLength: Int
    let hash: Data
    let payload: Data
}

struct BleFragment {
    let transferId: UInt32
    let index: Int
    let count: Int
    let payload: Data
}

enum ClipboardProtocol {
    static let maxText = 1024 * 1024
    static let maxImage = 32 * 1024 * 1024
    static let bleHeader = 10
    static let wireHeader = 70
    static let minChunk = 256
    static let maxChunk = 1024 * 1024
    static let maxWireFrame = wireHeader + maxChunk
    static let maxInflight = 8
    private static let magic = Data("CPH1".utf8)

    static func text(_ value: String) throws -> ClipboardContent {
        let data = Data(value.utf8)
        try validate(.text, data)
        return ClipboardContent(kind: .text, data: data)
    }

    static func image(_ kind: ClipboardKind, _ data: Data) throws -> ClipboardContent {
        guard kind != .text else { throw ProtocolError.invalidContent }
        try validate(kind, data)
        return ClipboardContent(kind: kind, data: data)
    }

    static func validate(_ kind: ClipboardKind, _ data: Data) throws {
        guard !data.isEmpty else { throw ProtocolError.invalidContent }
        let limit = kind == .text ? maxText : maxImage
        guard data.count <= limit else { throw ProtocolError.tooLarge }

        switch kind {
        case .text:
            guard String(data: data, encoding: .utf8) != nil else {
                throw ProtocolError.invalidContent
            }
        case .png:
            guard data.count >= 8,
                  Array(data.prefix(8)) == [137, 80, 78, 71, 13, 10, 26, 10] else {
                throw ProtocolError.invalidContent
            }
        case .jpeg:
            guard data.count >= 4,
                  data[data.startIndex] == 0xFF,
                  data[data.index(after: data.startIndex)] == 0xD8,
                  data[data.index(data.endIndex, offsetBy: -2)] == 0xFF,
                  data[data.index(before: data.endIndex)] == 0xD9 else {
                throw ProtocolError.invalidContent
            }
        case .webp:
            guard data.count >= 12,
                  String(data: data.prefix(4), encoding: .ascii) == "RIFF",
                  String(data: data.dropFirst(8).prefix(4), encoding: .ascii) == "WEBP" else {
                throw ProtocolError.invalidContent
            }
        }
    }

    static func encode(_ content: ClipboardContent, chunkSize: Int = 64 * 1024) throws -> [Data] {
        guard (minChunk...maxChunk).contains(chunkSize) else {
            throw ProtocolError.invalidFrame
        }
        try validate(content.kind, content.data)

        var transferId = Data(count: 16)
        transferId.withUnsafeMutableBytes { buffer in
            _ = SecRandomCopyBytes(kSecRandomDefault, 16, buffer.baseAddress!)
        }

        let hash = content.hash
        let count = (content.data.count + chunkSize - 1) / chunkSize
        var frames: [Data] = []
        frames.reserveCapacity(count)

        for index in 0..<count {
            let offset = index * chunkSize
            let length = min(chunkSize, content.data.count - offset)
            var frame = Data()
            frame.append(magic)
            frame.append(1)
            frame.append(transferId)
            frame.append(content.kind.rawValue)
            frame.appendInt32LE(Int32(index))
            frame.appendInt32LE(Int32(count))
            frame.appendInt32LE(Int32(content.data.count))
            frame.append(hash)
            frame.appendInt32LE(Int32(length))
            frame.append(content.data.subdata(in: offset..<(offset + length)))
            frames.append(frame)
        }

        return frames
    }

    static func decode(_ frame: Data) throws -> ClipboardWireChunk {
        guard (wireHeader...maxWireFrame).contains(frame.count),
              frame.prefix(4) == magic,
              frame[4] == 1 else {
            throw ProtocolError.invalidFrame
        }

        let transferId = frame.subdata(in: 5..<21)
        guard let kind = ClipboardKind(rawValue: frame[21]) else {
            throw ProtocolError.invalidFrame
        }
        let index = Int(frame.int32LE(at: 22))
        let count = Int(frame.int32LE(at: 26))
        let total = Int(frame.int32LE(at: 30))
        let hash = frame.subdata(in: 34..<66)
        let payloadLength = Int(frame.int32LE(at: 66))

        let maxTotal = kind == .text ? maxText : maxImage
        let maxCount = (maxTotal + 255) / 256

        guard count > 0,
              count <= maxCount,
              index >= 0,
              index < count,
              total > 0,
              total <= maxTotal,
              payloadLength > 0,
              payloadLength <= maxChunk,
              payloadLength <= total,
              frame.count == wireHeader + payloadLength else {
            throw ProtocolError.invalidFrame
        }

        return ClipboardWireChunk(
            transferId: transferId,
            kind: kind,
            index: index,
            count: count,
            totalLength: total,
            hash: hash,
            payload: frame.subdata(in: 70..<frame.count)
        )
    }

    static func splitBle(_ message: Data, maxGattValue: Int, transferId: UInt32) throws -> [Data] {
        guard maxGattValue > bleHeader else { throw ProtocolError.invalidFrame }
        let payloadSize = maxGattValue - bleHeader
        let count = (message.count + payloadSize - 1) / payloadSize
        guard count > 0, count <= Int(UInt16.max) else { throw ProtocolError.tooLarge }

        return try (0..<count).map { index in
            let offset = index * payloadSize
            let length = min(payloadSize, message.count - offset)
            var frame = Data([0xC7, 1])
            frame.appendUInt32LE(transferId)
            frame.appendUInt16LE(UInt16(index))
            frame.appendUInt16LE(UInt16(count))
            frame.append(message.subdata(in: offset..<(offset + length)))
            return frame
        }
    }

    static func decodeBle(_ frame: Data) throws -> BleFragment {
        guard frame.count >= bleHeader,
              frame[0] == 0xC7,
              frame[1] == 1 else {
            throw ProtocolError.invalidFrame
        }

        let transferId = frame.uint32LE(at: 2)
        let index = Int(frame.uint16LE(at: 6))
        let count = Int(frame.uint16LE(at: 8))
        guard count > 0, index < count else { throw ProtocolError.invalidFrame }

        return BleFragment(
            transferId: transferId,
            index: index,
            count: count,
            payload: frame.subdata(in: bleHeader..<frame.count)
        )
    }

    enum ProtocolError: Error {
        case invalidContent
        case invalidFrame
        case tooLarge
        case integrity
    }
}

final class ClipboardWireReassembler {
    private struct Pending {
        let kind: ClipboardKind
        let count: Int
        let total: Int
        let hash: Data
        let created = Date()
        var parts: [Int: Data] = [:]
        var bytes = 0
    }

    private var pending: [Data: Pending] = [:]
    private var completed: [Data: Date] = [:]

    func add(_ chunk: ClipboardWireChunk) throws -> ClipboardContent? {
        cleanup()

        guard completed[chunk.transferId] == nil else {
            throw ClipboardProtocol.ProtocolError.invalidFrame
        }

        if pending[chunk.transferId] == nil &&
            pending.count >= ClipboardProtocol.maxInflight {
            throw ClipboardProtocol.ProtocolError.tooLarge
        }

        var state = pending[chunk.transferId] ?? Pending(
            kind: chunk.kind,
            count: chunk.count,
            total: chunk.totalLength,
            hash: chunk.hash
        )

        guard state.kind == chunk.kind,
              state.count == chunk.count,
              state.total == chunk.totalLength,
              state.hash == chunk.hash else {
            pending.removeValue(forKey: chunk.transferId)
            throw ClipboardProtocol.ProtocolError.invalidFrame
        }

        if state.parts[chunk.index] == nil {
            guard state.bytes + chunk.payload.count <= state.total else {
                pending.removeValue(forKey: chunk.transferId)
                throw ClipboardProtocol.ProtocolError.tooLarge
            }
            state.parts[chunk.index] = chunk.payload
            state.bytes += chunk.payload.count
        }
        pending[chunk.transferId] = state

        guard state.parts.count == state.count else { return nil }

        var result = Data()
        result.reserveCapacity(state.total)
        for index in 0..<state.count {
            guard let part = state.parts[index] else { return nil }
            result.append(part)
        }
        pending.removeValue(forKey: chunk.transferId)

        guard result.count == state.total,
              Data(SHA256.hash(data: result)) == state.hash else {
            throw ClipboardProtocol.ProtocolError.integrity
        }

        try ClipboardProtocol.validate(state.kind, result)
        let content = ClipboardContent(kind: state.kind, data: result)
        completed[chunk.transferId] = Date()
        return content
    }

    private func cleanup() {
        let cutoff = Date().addingTimeInterval(-30)
        pending = pending.filter { $0.value.created >= cutoff }
        completed = completed.filter { $0.value >= cutoff }
    }
}

final class BleReassembler {
    private struct Pending {
        let count: Int
        let created = Date()
        var parts: [Int: Data] = [:]
        var bytes = 0
    }

    private var pending: [UInt32: Pending] = [:]
    private var completed: [UInt32: Date] = [:]

    func add(_ fragment: BleFragment) throws -> Data? {
        let cutoff = Date().addingTimeInterval(-30)
        pending = pending.filter { $0.value.created >= cutoff }
        completed = completed.filter { $0.value >= cutoff }

        guard completed[fragment.transferId] == nil else {
            throw ClipboardProtocol.ProtocolError.invalidFrame
        }

        if pending[fragment.transferId] == nil &&
            pending.count >= ClipboardProtocol.maxInflight {
            throw ClipboardProtocol.ProtocolError.tooLarge
        }

        var state = pending[fragment.transferId] ?? Pending(count: fragment.count)
        guard state.count == fragment.count else {
            pending.removeValue(forKey: fragment.transferId)
            throw ClipboardProtocol.ProtocolError.invalidFrame
        }

        if let existing = state.parts[fragment.index] {
            guard existing == fragment.payload else {
                pending.removeValue(forKey: fragment.transferId)
                throw ClipboardProtocol.ProtocolError.invalidFrame
            }
        } else {
            guard state.bytes + fragment.payload.count <= ClipboardProtocol.maxWireFrame else {
                pending.removeValue(forKey: fragment.transferId)
                throw ClipboardProtocol.ProtocolError.tooLarge
            }
            state.parts[fragment.index] = fragment.payload
            state.bytes += fragment.payload.count
        }
        pending[fragment.transferId] = state
        guard state.parts.count == state.count else { return nil }

        var result = Data()
        for index in 0..<state.count {
            guard let part = state.parts[index] else { return nil }
            result.append(part)
        }
        pending.removeValue(forKey: fragment.transferId)
        completed[fragment.transferId] = Date()
        return result
    }
}

extension Data {
    mutating func appendUInt16LE(_ value: UInt16) {
        var v = value.littleEndian
        Swift.withUnsafeBytes(of: &v) { append(contentsOf: $0) }
    }

    mutating func appendUInt32LE(_ value: UInt32) {
        var v = value.littleEndian
        Swift.withUnsafeBytes(of: &v) { append(contentsOf: $0) }
    }

    mutating func appendInt32LE(_ value: Int32) {
        var v = value.littleEndian
        Swift.withUnsafeBytes(of: &v) { append(contentsOf: $0) }
    }

    func uint16LE(at offset: Int) -> UInt16 {
        let value: UInt16 = self.withUnsafeBytes {
            $0.loadUnaligned(fromByteOffset: offset, as: UInt16.self)
        }
        return UInt16(littleEndian: value)
    }

    func uint32LE(at offset: Int) -> UInt32 {
        let value: UInt32 = self.withUnsafeBytes {
            $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self)
        }
        return UInt32(littleEndian: value)
    }

    func int32LE(at offset: Int) -> Int32 {
        let value: Int32 = self.withUnsafeBytes {
            $0.loadUnaligned(fromByteOffset: offset, as: Int32.self)
        }
        return Int32(littleEndian: value)
    }
}

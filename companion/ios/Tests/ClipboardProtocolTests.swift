import XCTest
@testable import ConPhoneCompanion

final class ClipboardProtocolTests: XCTestCase {
    func testTextRoundTripOutOfOrder() throws {
        let source = try ClipboardProtocol.text(String(repeating: "한글🙂abc", count: 300))
        let frames = try ClipboardProtocol.encode(source, chunkSize: 256)
        XCTAssertGreaterThan(frames.count, 1)

        let reassembler = ClipboardWireReassembler()
        var result: ClipboardContent?

        for frame in frames.reversed() {
            if let content = try reassembler.add(ClipboardProtocol.decode(frame)) {
                result = content
            }
        }

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.kind, .text)
        XCTAssertEqual(result?.data, source.data)
        XCTAssertEqual(result?.hash, source.hash)
    }

    func testPngSignatureValidation() throws {
        let png = Data([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4])
        XCTAssertNoThrow(try ClipboardProtocol.image(.png, png))

        XCTAssertThrowsError(
            try ClipboardProtocol.image(
                .png,
                Data([1, 2, 3, 4, 5, 6, 7, 8])))
    }

    func testAckRoundTrip() throws {
        let id = Data((0..<16).map { UInt8($0) })
        let ack = ClipboardProtocol.encodeAck(id)
        XCTAssertEqual(ack.count, ClipboardProtocol.ackLength)
        XCTAssertEqual(ClipboardProtocol.decodeAck(ack), id)

        var invalid = ack
        invalid[0] = 0
        XCTAssertNil(ClipboardProtocol.decodeAck(invalid))
    }

    func testCompletedTransferReplayRejected() throws {
        let source = try ClipboardProtocol.text(String(repeating: "replay-test", count: 100))
        let frames = try ClipboardProtocol.encode(source, chunkSize: 256)
        let reassembler = ClipboardWireReassembler()

        var result: ClipboardContent?
        for frame in frames {
            if let content = try reassembler.add(ClipboardProtocol.decode(frame)) {
                result = content
            }
        }
        XCTAssertNotNil(result)

        XCTAssertThrowsError(
            try reassembler.add(ClipboardProtocol.decode(frames[0])))
    }

    func testConflictingDuplicateChunkRejected() throws {
        let source = try ClipboardProtocol.text(String(repeating: "duplicate-test", count: 100))
        let frames = try ClipboardProtocol.encode(source, chunkSize: 256)
        let chunk = try ClipboardProtocol.decode(frames[0])
        let reassembler = ClipboardWireReassembler()

        XCTAssertNil(try reassembler.add(chunk))

        var changed = chunk.payload
        changed[0] ^= 1
        let conflicting = ClipboardWireChunk(
            transferId: chunk.transferId,
            kind: chunk.kind,
            index: chunk.index,
            count: chunk.count,
            totalLength: chunk.totalLength,
            hash: chunk.hash,
            payload: changed)

        XCTAssertThrowsError(try reassembler.add(conflicting))
    }

    func testBleFragmentRoundTripOutOfOrder() throws {
        let source = try ClipboardProtocol.text(String(repeating: "ble", count: 300))
        let frames = try ClipboardProtocol.encode(source, chunkSize: 4096)
        XCTAssertEqual(frames.count, 1)
        let wire = frames[0]

        let fragments = try ClipboardProtocol.splitBle(
            wire,
            maxGattValue: 20,
            transferId: 42)
        XCTAssertGreaterThan(fragments.count, 1)
        XCTAssertTrue(fragments.allSatisfy { $0.count <= 20 })

        let reassembler = BleReassembler()
        var result: Data?

        for frame in fragments.reversed() {
            if let data = try reassembler.add(ClipboardProtocol.decodeBle(frame)) {
                result = data
            }
        }

        XCTAssertEqual(result, wire)
    }

    func testConflictingBleDuplicateRejected() throws {
        let source = try ClipboardProtocol.text(String(repeating: "ble-dup", count: 100))
        let wire = try ClipboardProtocol.encode(source, chunkSize: 4096)[0]
        let fragments = try ClipboardProtocol.splitBle(
            wire,
            maxGattValue: 20,
            transferId: 777)
        let first = try ClipboardProtocol.decodeBle(fragments[0])
        let reassembler = BleReassembler()

        XCTAssertNil(try reassembler.add(first))

        var changed = first.payload
        changed[0] ^= 1
        let conflicting = BleFragment(
            transferId: first.transferId,
            index: first.index,
            count: first.count,
            payload: changed)

        XCTAssertThrowsError(try reassembler.add(conflicting))
    }
}

package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class BleCleanupProtocolTest {
    @Test fun partialClipboardIsDroppedButReplayHistorySurvivesCleanup() {
        val content = ClipboardProtocol.text("재연결🙂 " + "x".repeat(1000))
        val chunks = ClipboardProtocol.encodeSequence(content, 256).map(ClipboardProtocol::decode).toList()
        val wire = ClipboardWireReassembler()
        wire.add(chunks.first()); wire.clearPending()
        chunks.drop(1).forEach { assertNull(wire.add(it)) }
        assertArrayEquals(content.data, wire.add(chunks.first())!!.data)
        wire.clearPending()
        assertThrows(IllegalArgumentException::class.java) { wire.add(chunks.first()) }
    }

    @Test fun partialMtuFragmentsAreDroppedWithoutResettingReplayProtection() {
        val fragments = ClipboardProtocol.splitBle(ByteArray(100), 20, 417u).map(ClipboardProtocol::decodeBle)
        val ble = BleReassembler()
        ble.add(fragments.first()); ble.clearPending()
        fragments.drop(1).forEach { assertNull(ble.add(it)) }
        assertEquals(100, ble.add(fragments.first())!!.size)
        ble.clearPending()
        assertThrows(IllegalArgumentException::class.java) { ble.add(fragments.first()) }
    }

    @Test fun receiptCacheCannotAcknowledgeDiscardedAssemblyOnNewConnection() {
        val packet = ClipboardProtocol.splitBle(ByteArray(100), 20, 991u).first().also { it[1] = 2 }
        val receiver = ReliableBleReceiver()
        var consumed = 0
        receiver.receive(packet) { consumed++; null }
        receiver.receive(packet) { consumed++; null }
        assertEquals(1, consumed)
        receiver.reset()
        receiver.receive(packet) { consumed++; null }
        assertEquals(2, consumed)
    }
}

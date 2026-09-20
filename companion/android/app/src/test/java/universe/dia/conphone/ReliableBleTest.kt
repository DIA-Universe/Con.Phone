package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class ReliableBleTest {
    @Test fun windowsImageRecoversDroppedPacketsAndReceipts() {
        for (mtu in listOf(182, 512)) {
            val expected = javaClass.getResourceAsStream("/source.png")!!.readBytes()
            val packets = javaClass.getResourceAsStream("/frames-$mtu.txt")!!.bufferedReader().readLines()
                .filter { it.isNotBlank() }.map { hex ->
                    hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray().also { it[1] = 2 }
                }
            val receiver = ReliableBleReceiver()
            val ble = BleReassembler()
            val wire = ClipboardWireReassembler()
            var applies = 0
            var actual: ByteArray? = null
            val consume: (ByteArray) -> ByteArray? = { packet ->
                val frame = ble.add(ClipboardProtocol.decodeBle(packet))
                if (frame == null) null else {
                    val chunk = ClipboardProtocol.decode(frame)
                    val content = wire.add(chunk)
                    if (content == null) null else {
                        applies++
                        actual = content.data
                        chunk.transferId
                    }
                }
            }
            for ((index, packet) in packets.withIndex()) {
                // A dropped notification does not reach the receiver; retransmit it.
                var receipt: ReliableBleReceiver.Result? = null
                if (index != 5) receipt = receiver.receive(packet, consume)
                if (receipt == null) receipt = receiver.receive(packet, consume)
                assertArrayEquals(packet.copyOfRange(0, 8), receipt.receipt)
                // Lose every receipt once, including the final application ACK.
                val duplicate = receiver.receive(packet, consume)
                assertArrayEquals(receipt.receipt, duplicate.receipt)
                assertArrayEquals(receipt.applicationAck, duplicate.applicationAck)
            }
            assertArrayEquals(expected, actual)
            assertEquals(1, applies)
            assertNotNull(receiver.receive(packets.last(), consume).applicationAck)
            assertEquals(1, applies)
        }
    }

    @Test fun failedProcessingDoesNotProduceReceiptOrCacheSuccess() {
        val packet = ClipboardProtocol.splitBle(byteArrayOf(1, 2, 3), 182, 17u)
            .first().also { it[1] = 2 }
        val receiver = ReliableBleReceiver()
        try {
            receiver.receive(packet) { error("application failed") }
            fail("must propagate failure")
        } catch (_: IllegalStateException) { }
        var consumed = false
        receiver.receive(packet) { consumed = true; null }
        assertTrue(consumed)
    }
}

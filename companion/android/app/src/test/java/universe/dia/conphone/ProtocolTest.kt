package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class ProtocolTest {
    @Test
    fun p2pSessionKeyMatchesWindowsVector() {
        val secret = ByteArray(32) { it.toByte() }
        val deviceHash = ByteArray(32) { (it + 32).toByte() }
        val clientNonce = ByteArray(16) { it.toByte() }
        val serverNonce = ByteArray(16) { (it + 16).toByte() }

        val key = P2PSecurity.deriveSessionKey(
            secret,
            deviceHash,
            clientNonce,
            serverNonce
        )

        assertEquals(
            "16BE739CCB5056785C93854CFD3C384105BE7F799292A5BEBA8FFE2BE73248D0",
            key.joinToString("") { "%02X".format(it) }
        )
    }

    @Test
    fun textRoundTripOutOfOrder() {
        val source = ClipboardProtocol.text("한글🙂abc".repeat(300))
        val frames = ClipboardProtocol.encode(source, 256)
        assertTrue(frames.size > 1)

        val reassembler = ClipboardWireReassembler()
        var result: ClipboardContent? = null

        for (frame in frames.asReversed()) {
            val content = reassembler.add(ClipboardProtocol.decode(frame))
            if (content != null) result = content
        }

        assertNotNull(result)
        assertEquals(ClipboardKind.TEXT, result!!.kind)
        assertArrayEquals(source.data, result!!.data)
        assertArrayEquals(source.hash, result!!.hash)
    }

    @Test
    fun pngSignatureValidation() {
        val png = byteArrayOf(
            137.toByte(), 80, 78, 71, 13, 10, 26, 10,
            1, 2, 3, 4
        )
        val content = ClipboardProtocol.image(ClipboardKind.PNG, png)
        assertEquals(ClipboardKind.PNG, content.kind)

        assertFails {
            ClipboardProtocol.image(
                ClipboardKind.PNG,
                byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
            )
        }
    }

    @Test
    fun ackRoundTrip() {
        val id = ByteArray(16) { it.toByte() }
        val ack = ClipboardProtocol.encodeAck(id)
        assertEquals(ClipboardProtocol.ACK_LENGTH, ack.size)
        assertArrayEquals(id, ClipboardProtocol.decodeAck(ack))

        val invalid = ack.copyOf()
        invalid[0] = 0
        assertNull(ClipboardProtocol.decodeAck(invalid))
    }

    @Test
    fun completedTransferReplayRejected() {
        val source = ClipboardProtocol.text("replay-test".repeat(100))
        val frames = ClipboardProtocol.encode(source, 256)
        val reassembler = ClipboardWireReassembler()

        var result: ClipboardContent? = null
        for (frame in frames) {
            val content = reassembler.add(ClipboardProtocol.decode(frame))
            if (content != null) result = content
        }
        assertNotNull(result)

        assertFails {
            reassembler.add(ClipboardProtocol.decode(frames.first()))
        }
    }

    @Test
    fun conflictingDuplicateChunkRejected() {
        val source = ClipboardProtocol.text("duplicate-test".repeat(100))
        val frames = ClipboardProtocol.encode(source, 256)
        val chunk = ClipboardProtocol.decode(frames.first())
        val reassembler = ClipboardWireReassembler()

        assertNull(reassembler.add(chunk))

        val changed = chunk.payload.copyOf()
        changed[0] = (changed[0].toInt() xor 1).toByte()

        assertFails {
            reassembler.add(chunk.copy(payload = changed))
        }
    }

    @Test
    fun bleFragmentRoundTripOutOfOrder() {
        val source = ClipboardProtocol.text("ble".repeat(300))
        val wire = ClipboardProtocol.encode(source, 4096).single()
        val fragments = ClipboardProtocol.splitBle(
            wire,
            maxGattValue = 20,
            transferId = 42u
        )
        assertTrue(fragments.size > 1)
        assertTrue(fragments.all { it.size <= 20 })

        val reassembler = BleReassembler()
        var result: ByteArray? = null

        for (frame in fragments.asReversed()) {
            val value = reassembler.add(ClipboardProtocol.decodeBle(frame))
            if (value != null) result = value
        }

        assertArrayEquals(wire, result)
    }

    @Test
    fun conflictingBleDuplicateRejected() {
        val source = ClipboardProtocol.text("ble-dup".repeat(100))
        val wire = ClipboardProtocol.encode(source, 4096).single()
        val fragments = ClipboardProtocol.splitBle(wire, 20, 777u)
        val first = ClipboardProtocol.decodeBle(fragments.first())
        val reassembler = BleReassembler()

        assertNull(reassembler.add(first))
        val changed = first.payload.copyOf()
        changed[0] = (changed[0].toInt() xor 1).toByte()

        assertFails {
            reassembler.add(first.copy(payload = changed))
        }
    }

    @Test
    fun oversizedDeclaredTotalRejected() {
        val source = ClipboardProtocol.text("metadata")
        val frame = ClipboardProtocol.encode(source, 256).single()
        val changed = frame.copyOf()

        // totalLength starts at byte offset 30 and is little endian.
        val value = ClipboardProtocol.MAX_IMAGE + 1
        changed[30] = (value and 0xff).toByte()
        changed[31] = ((value ushr 8) and 0xff).toByte()
        changed[32] = ((value ushr 16) and 0xff).toByte()
        changed[33] = ((value ushr 24) and 0xff).toByte()

        assertFails { ClipboardProtocol.decode(changed) }
    }

    private fun assertFails(block: () -> Unit) {
        var failed = false
        try {
            block()
        } catch (_: Throwable) {
            failed = true
        }
        assertTrue("Expected operation to fail", failed)
    }
}

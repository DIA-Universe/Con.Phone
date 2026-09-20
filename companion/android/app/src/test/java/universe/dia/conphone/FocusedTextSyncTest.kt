package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class FocusedTextSyncTest {
    @Test fun localRemoteLocalRecopyIsNotPermanentlySuppressed() {
        val gate = FocusedTextSync()
        val sent = mutableListOf<String>()
        assertTrue(gate.capture("폰 텍스트", false, false) { sent.add(it) })
        assertFalse(gate.capture("폰 텍스트", false, false) { sent.add(it) })
        assertFalse(gate.capture("PC 텍스트", true, false) { sent.add(it) })
        assertTrue(gate.capture("폰 텍스트", false, false) { sent.add(it) })
        assertEquals(listOf("폰 텍스트", "폰 텍스트"), sent)
    }
    @Test fun failedQueueWriteCanRetryAndImagesNeverQueue() {
        val gate = FocusedTextSync()
        try { gate.capture("text", false, false) { error("disk busy") } } catch (_: IllegalStateException) { }
        assertTrue(gate.capture("text", false, false) { })
        assertFalse(gate.capture(null, false, false) { fail("image must not queue") })
        assertTrue(gate.capture("text", false, false) { })
        assertTrue(gate.capture("text", true, true) { })
    }
}

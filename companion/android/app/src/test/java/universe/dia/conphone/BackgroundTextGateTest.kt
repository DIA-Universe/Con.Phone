package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class BackgroundTextGateTest {
    @Test fun copiesAndRemoteEchoes() {
        val gate = BackgroundTextGate()
        val sent = mutableListOf<String>()
        fun capture(time: Long, text: String?, remote: Boolean = false) = gate.capture(time, text, remote) { sent.add(it) }
        assertTrue(capture(1, "한글🙂"))
        assertFalse(capture(1, "한글🙂"))
        assertFalse(capture(2, "PC text", true))
        assertTrue(capture(3, "한글🙂"))
        assertTrue(capture(4, "한글🙂"))
        assertFalse(capture(5, null))
        assertEquals(listOf("한글🙂", "한글🙂", "한글🙂"), sent)
    }
    @Test fun failedWritesCanRetry() {
        val gate = BackgroundTextGate()
        runCatching { gate.capture(1, "retry", false) { error("disk") } }
        assertTrue(gate.capture(1, "retry", false) {})
    }
}

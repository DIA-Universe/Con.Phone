package universe.dia.conphone

import java.nio.file.Files
import org.junit.Assert.*
import org.junit.Test

class BleDiagnosticLogTest {
    @Test fun preservesConnectionReasonAcrossServiceInstances() {
        val dir = Files.createTempDirectory("linko-log").toFile()
        try {
            BleDiagnosticLog(dir).write("connection status=8 state=0")
            BleDiagnosticLog(dir).write("reconnect scheduled: 1000ms")
            val text = BleDiagnosticLog(dir).snapshot()
            assertTrue(text.indexOf("status=8") < text.indexOf("reconnect scheduled"))
            assertEquals(2, text.lines().count { it.isNotBlank() })
        } finally { dir.deleteRecursively() }
    }

    @Test fun rotationKeepsRecentFailuresAndBoundsHistory() {
        val dir = Files.createTempDirectory("linko-log").toFile()
        try {
            val log = BleDiagnosticLog(dir, 100)
            repeat(50) { log.write("failure-$it " + "x".repeat(200)) }
            assertFalse(log.snapshot().contains("failure-0 "))
            assertTrue(log.snapshot().contains("failure-48 "))
            assertTrue(log.snapshot().contains("failure-49 "))
            assertTrue(dir.listFiles()!!.sumOf { it.length() } < 1000)
        } finally { dir.deleteRecursively() }
    }
}

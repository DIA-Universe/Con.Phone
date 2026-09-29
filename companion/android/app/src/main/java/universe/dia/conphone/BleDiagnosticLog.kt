package universe.dia.conphone

import java.io.File
import java.time.Instant

/** Only callers' connection codes/states belong here; never payloads or device addresses. */
internal class BleDiagnosticLog(private val directory: File, private val limit: Long = 64 * 1024) {
    @Synchronized fun write(message: String) {
        directory.mkdirs()
        val current = File(directory, "connection.log")
        val previous = File(directory, "connection.previous.log")
        if (current.length() >= limit) {
            current.copyTo(previous, overwrite = true)
            current.writeText("")
        }
        current.appendText("${Instant.now()} ${message.take(2048)}\n")
    }

    @Synchronized fun snapshot(): String = listOf("connection.previous.log", "connection.log")
        .map { File(directory, it) }.filter { it.isFile }.joinToString("") { it.readText() }
}

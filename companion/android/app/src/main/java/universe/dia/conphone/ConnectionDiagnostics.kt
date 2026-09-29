package universe.dia.conphone

import android.content.Context
import android.os.Handler
import android.os.Looper
import java.io.File
import java.util.concurrent.Executors

internal object ConnectionDiagnostics {
    private val worker = Executors.newSingleThreadExecutor()
    private fun log(context: Context) = BleDiagnosticLog(File(context.filesDir, "ble-diagnostics"))

    fun write(context: Context, message: String) {
        val app = context.applicationContext
        worker.execute { runCatching { log(app).write(message) } }
    }

    fun snapshot(context: Context, ready: (String) -> Unit) {
        val app = context.applicationContext
        worker.execute {
            val text = runCatching { log(app).snapshot() }.getOrDefault("연결 로그를 읽지 못했습니다.")
            Handler(Looper.getMainLooper()).post { ready(text.ifBlank { "저장된 연결 로그가 없습니다." }) }
        }
    }
}

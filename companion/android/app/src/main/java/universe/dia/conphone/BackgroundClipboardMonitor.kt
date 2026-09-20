package universe.dia.conphone
import com.linko.ipc.IClipboardReader

import android.app.KeyguardManager
import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import rikka.shizuku.Shizuku
import java.util.concurrent.Executors

/** Permission requests belong to the visible settings UI. This monitor never prompts. */
class BackgroundClipboardMonitor(private val context: Context, private val drain: () -> Unit) {
    companion object {
        private const val PREFS = "linko_background_clipboard"
        @Volatile var status = "자동 복사 설정이 필요합니다."
            private set
        @Volatile var active = false
            private set
        fun enabled(context: Context) = context.getSharedPreferences(PREFS, 0).getBoolean("enabled", false)
        fun setEnabled(context: Context, value: Boolean) {
            context.getSharedPreferences(PREFS, 0).edit().putBoolean("enabled", value).apply()
        }
        fun approved() = runCatching {
            Shizuku.pingBinder() && Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED
        }.getOrDefault(false)
    }
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    private val gate = BackgroundTextGate()
    private val args = Shizuku.UserServiceArgs(ComponentName(context, ApprovedClipboardReader::class.java))
        .daemon(false).processNameSuffix("clipboard_reader").tag("linko.clipboard.reader").version(10)
    private var reader: IClipboardReader? = null
    private var binding = false
    private var busy = false
    private var closed = false
    private var bindAt = 0L
    private var generation = 0
    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            if (closed || !enabled(context) || !approved()) { disconnect(); return }
            reader = IClipboardReader.Stub.asInterface(binder)
            binding = false
        }
        override fun onServiceDisconnected(name: ComponentName?) {
            reader = null; binding = false; active = false; generation++
        }
    }
    private fun disconnect() {
        generation++
        if (reader != null || binding) runCatching { Shizuku.unbindUserService(args, connection, true) }
        reader = null; binding = false; active = false
    }
    private val tick = object : Runnable {
        override fun run() {
            if (closed) return
            main.postDelayed(this, 750)
            if (!enabled(context) || !approved()) {
                status = if (enabled(context)) "Shizuku를 시작하고 LINKO 권한을 허용하세요." else "자동 복사 설정이 필요합니다."
                disconnect(); return
            }
            if (context.getSystemService(KeyguardManager::class.java).isDeviceLocked) {
                active = false; status = "잠금 해제 후 자동 복사를 재개합니다."; return
            }
            if (reader == null) {
                active = false; status = "자동 복사 연결 중…"
                if (binding && android.os.SystemClock.elapsedRealtime() - bindAt > 15_000) disconnect()
                if (!binding) runCatching {
                    binding = true; bindAt = android.os.SystemClock.elapsedRealtime()
                    Shizuku.bindUserService(args, connection)
                }.onFailure { binding = false; status = "Shizuku 연결 실패. 실행 상태와 권한을 확인하세요." }
                return
            }
            if (busy) return
            busy = true
            val current = reader!!
            val epoch = generation
            worker.execute {
                val result = runCatching { current.readClipboard() }
                main.post {
                    busy = false
                    if (closed || epoch != generation || !enabled(context) || !approved()) return@post
                    if (context.getSystemService(KeyguardManager::class.java).isDeviceLocked) {
                        active = false; status = "잠금 해제 후 자동 복사를 재개합니다."; return@post
                    }
                    result.onSuccess { snapshot ->
                        val error = snapshot.getString("error")
                        if (error != null) { active = false; status = error; return@onSuccess }
                        active = true; status = "다른 앱에서 복사한 텍스트를 자동 전송합니다."
                        val text = snapshot.getString("text")
                        val label = snapshot.getString("label").orEmpty()
                        val remote = (label.startsWith("LINKO") || label.startsWith("Con.Phone")) &&
                            text == ClipboardBridgeService.lastReceivedText
                        runCatching {
                            if (gate.capture(snapshot.getLong("timestamp"), text, remote) {
                                AndroidSharedPayloadStore(context).saveText(it)
                            }) drain()
                        }.onFailure { status = "복사 내용을 저장하지 못했습니다. 다시 시도합니다." }
                    }.onFailure { disconnect(); status = "Shizuku 연결이 끊겼습니다. 다시 연결합니다." }
                }
            }
        }
    }
    fun start() { main.post(tick) }
    fun close() {
        closed = true; main.removeCallbacksAndMessages(null); disconnect(); worker.shutdownNow()
    }
}

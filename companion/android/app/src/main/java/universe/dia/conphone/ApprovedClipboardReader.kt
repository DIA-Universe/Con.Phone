package universe.dia.conphone
import com.linko.ipc.IClipboardReader

import android.content.ClipData
import android.content.Context
import android.os.Binder
import android.os.Bundle
import android.os.IBinder
import android.os.Process

/** Runs only in the user-approved Shizuku shell process, never in the normal app. */
class ApprovedClipboardReader(context: Context) : IClipboardReader.Stub() {
    private val ownerUid = context.applicationInfo.uid
    private var clipboard: Any? = null
    private var readMethod: java.lang.reflect.Method? = null

    override fun readClipboard(): Bundle {
        check(Binder.getCallingUid() == ownerUid) { "Unexpected caller" }
        return try {
            // The background-read permission belongs to the shell UID/package.
            check(Process.myUid() == 2000) { "Start Shizuku using wireless or USB debugging" }
            if (clipboard == null) {
                val binder = Class.forName("android.os.ServiceManager")
                    .getMethod("getService", String::class.java).invoke(null, "clipboard") as IBinder
                clipboard = Class.forName("android.content.IClipboard\$Stub")
                    .getMethod("asInterface", IBinder::class.java).invoke(null, binder)
                val api = Class.forName("android.content.IClipboard")
                readMethod = api.methods.singleOrNull { method ->
                    method.name == "getPrimaryClip" &&
                        method.parameterTypes.toList() in listOf(
                            listOf(String::class.java, String::class.java, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType),
                            listOf(String::class.java, String::class.java, Int::class.javaPrimitiveType))
                } ?: error("Unsupported clipboard API")
            }
            val method = checkNotNull(readMethod)
            val userId = ownerUid / 100000
            val args = if (method.parameterCount == 4) arrayOf<Any?>("com.android.shell", null, userId, 0)
                else arrayOf<Any?>("com.android.shell", null, userId)
            val clip = method.invoke(clipboard, *args) as? ClipData
            val text = if (clip != null && clip.itemCount > 0) clip.getItemAt(0).text?.toString() else null
            Bundle().apply {
                // Keep IPC comfortably below Binder's transaction limit; images/URIs never leave here.
                if (text != null && text.length > 100_000) putString("error", "텍스트가 너무 깁니다 (최대 10만 자).")
                else putString("text", text)
                putString("label", clip?.description?.label?.toString())
                putLong("timestamp", clip?.description?.timestamp ?: 0L)
            }
        } catch (ex: Exception) {
            clipboard = null
            readMethod = null
            Bundle().apply { putString("error", "이 기기에서 클립보드 읽기가 거부되었습니다. Shizuku 시작 방식과 권한을 확인하세요.") }
        }
    }

    override fun destroy() { kotlin.system.exitProcess(0) }
}

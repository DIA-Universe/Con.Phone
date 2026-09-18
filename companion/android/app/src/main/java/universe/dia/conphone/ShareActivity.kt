package universe.dia.conphone

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

class ShareActivity : Activity() {
    companion object {
        private const val MAX_STAGED_IMAGE_BYTES = 40L * 1024 * 1024
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (intent?.action != Intent.ACTION_SEND) {
            finish()
            return
        }

        when {
            intent.type?.startsWith("text/") == true -> shareText()
            intent.type?.startsWith("image/") == true -> shareImage()
            else -> {
                Toast.makeText(this, "텍스트 또는 이미지만 지원합니다.", Toast.LENGTH_SHORT).show()
                finish()
            }
        }
    }

    private fun shareText() {
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
        if (text.isNullOrEmpty()) {
            Toast.makeText(this, "공유할 텍스트가 없습니다.", Toast.LENGTH_SHORT).show()
            finish()
            return
        }

        startForegroundService(
            Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_SEND_SHARED_TEXT)
                .putExtra(ClipboardBridgeService.EXTRA_SHARED_TEXT, text)
        )
        Toast.makeText(this, "PC 전송 대기열에 추가했습니다.", Toast.LENGTH_SHORT).show()
        finish()
    }

    private fun shareImage() {
        val uri = sharedUri()
        if (uri == null) {
            Toast.makeText(this, "공유할 이미지를 찾지 못했습니다.", Toast.LENGTH_SHORT).show()
            finish()
            return
        }

        val directory = File(cacheDir, "shared").apply { mkdirs() }
        directory.listFiles()?.forEach {
            if (System.currentTimeMillis() - it.lastModified() > 24 * 60 * 60 * 1000L)
                runCatching { it.delete() }
        }

        val target = File(directory, "share-${UUID.randomUUID()}.bin")

        runCatching {
            contentResolver.openInputStream(uri).use { input ->
                requireNotNull(input) { "공유 이미지를 열 수 없습니다." }
                FileOutputStream(target).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var total = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        total += read
                        require(total <= MAX_STAGED_IMAGE_BYTES) {
                            "공유 이미지가 임시 저장 한도(40 MiB)를 초과합니다."
                        }
                        output.write(buffer, 0, read)
                    }
                }
            }

            target
        }.onSuccess { target ->
            startForegroundService(
                Intent(this, ClipboardBridgeService::class.java)
                    .setAction(ClipboardBridgeService.ACTION_SEND_SHARED_IMAGE)
                    .putExtra(ClipboardBridgeService.EXTRA_SHARED_IMAGE_PATH, target.absolutePath)
            )
            Toast.makeText(this, "PC 이미지 전송 대기열에 추가했습니다.", Toast.LENGTH_SHORT).show()
        }.onFailure {
            runCatching { target.delete() }
            Toast.makeText(this, "이미지 공유 준비 실패: ${it.message}", Toast.LENGTH_SHORT).show()
        }

        finish()
    }

    private fun sharedUri(): Uri? =
        if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(Intent.EXTRA_STREAM)
        }
}

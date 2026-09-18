package universe.dia.conphone

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream

class ShareToPcActivity : Activity() {
    companion object {
        private const val MAX_PIXELS = 40_000_000L
        private const val MAX_IMAGE_BYTES = 32 * 1024 * 1024
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (intent?.action != Intent.ACTION_SEND) {
            finish()
            return
        }

        Thread {
            runCatching { prepareClipboard(intent) }
                .onSuccess {
                    runOnUiThread {
                        startForegroundService(
                            Intent(this, ClipboardBridgeService::class.java)
                                .setAction(ClipboardBridgeService.ACTION_SEND)
                        )
                        Toast.makeText(
                            this,
                            "Con.Phone으로 PC 전송을 시작했습니다.",
                            Toast.LENGTH_SHORT
                        ).show()
                        finish()
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        Toast.makeText(
                            this,
                            "PC 전송 준비 실패: ${error.message}",
                            Toast.LENGTH_LONG
                        ).show()
                        finish()
                    }
                }
        }.start()
    }

    private fun prepareClipboard(source: Intent) {
        val clipboard = getSystemService(ClipboardManager::class.java)
        val mime = source.type.orEmpty()

        if (mime.startsWith("text/")) {
            val text = source.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
                ?.takeIf { it.isNotEmpty() }
                ?: error("공유할 텍스트가 없습니다.")

            val content = ClipboardProtocol.text(text)
            clipboard.setPrimaryClip(
                ClipData.newPlainText(
                    "Con.Phone share",
                    content.data.toString(Charsets.UTF_8)
                )
            )
            return
        }

        if (mime.startsWith("image/")) {
            val uri = source.sharedUri()
                ?: error("공유할 이미지가 없습니다.")
            normalizeImage(uri)
            clipboard.setPrimaryClip(
                ClipData.newUri(
                    contentResolver,
                    "Con.Phone image",
                    ClipboardImageProvider.URI
                )
            )
            return
        }

        error("텍스트와 이미지만 지원합니다.")
    }

    private fun normalizeImage(uri: Uri) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        contentResolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "이미지를 열 수 없습니다." }
            BitmapFactory.decodeStream(input, null, bounds)
        }

        require(bounds.outWidth > 0 && bounds.outHeight > 0) {
            "이미지 크기를 확인할 수 없습니다."
        }
        require(bounds.outWidth.toLong() * bounds.outHeight.toLong() <= MAX_PIXELS) {
            "이미지 해상도가 너무 큽니다."
        }

        val bitmap = contentResolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "이미지를 열 수 없습니다." }
            BitmapFactory.decodeStream(input)
        } ?: error("이미지를 디코딩할 수 없습니다.")

        try {
            val encoded = ByteArrayOutputStream()
            require(bitmap.compress(Bitmap.CompressFormat.PNG, 100, encoded)) {
                "이미지를 PNG로 변환하지 못했습니다."
            }
            val bytes = encoded.toByteArray()
            require(bytes.size <= MAX_IMAGE_BYTES) {
                "변환된 이미지가 32 MiB를 초과합니다."
            }

            ClipboardProtocol.image(ClipboardKind.PNG, bytes)
            FileOutputStream(File(cacheDir, ClipboardImageProvider.FILE_NAME)).use {
                it.write(bytes)
            }
        } finally {
            bitmap.recycle()
        }
    }

    @Suppress("DEPRECATION")
    private fun Intent.sharedUri(): Uri? =
        if (android.os.Build.VERSION.SDK_INT >= 33)
            getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        else
            getParcelableExtra(Intent.EXTRA_STREAM)
}

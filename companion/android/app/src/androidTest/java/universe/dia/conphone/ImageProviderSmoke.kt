package universe.dia.conphone

import android.app.Instrumentation
import android.content.ClipData
import android.content.ClipboardManager
import android.graphics.Bitmap
import android.os.Bundle
import android.provider.OpenableColumns

/** Exercises actual Android MIME negotiation and typed image streams. */
class ImageProviderSmoke : Instrumentation() {
    override fun onCreate(arguments: Bundle?) { super.onCreate(arguments); start() }
    override fun onStart() {
        val result = Bundle()
        try {
            val ctx = targetContext
            val (firstFile, firstUri) = ClipboardImageProvider.newImage(ctx)
            val bitmap = Bitmap.createBitmap(4, 3, Bitmap.Config.ARGB_8888)
            firstFile.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) }
            bitmap.recycle()
            val original = firstFile.readBytes()
            val (secondFile, secondUri) = ClipboardImageProvider.newImage(ctx)
            secondFile.writeBytes(original)
            check(firstUri != secondUri && firstUri.lastPathSegment!!.endsWith(".png"))
            check(ctx.contentResolver.getType(firstUri) == "image/png")
            check(ctx.contentResolver.getStreamTypes(firstUri, "image/*")!!.contains("image/png"))
            check(ctx.contentResolver.getStreamTypes(firstUri, "text/*") == null)
            ctx.contentResolver.openTypedAssetFileDescriptor(firstUri, "image/*", null)!!.use { descriptor ->
                descriptor.createInputStream().use { check(it.readBytes().contentEquals(original)) }
            }
            ctx.contentResolver.query(firstUri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)!!.use { cursor ->
                check(cursor.moveToFirst())
                check(cursor.getString(0).endsWith(".png"))
                check(cursor.getLong(1) == original.size.toLong())
            }
            runOnMainSync {
                val clip = ClipData.newUri(ctx.contentResolver, "Con.Phone image", firstUri)
                check(clip.description.hasMimeType("image/png"))
                ctx.getSystemService(ClipboardManager::class.java).setPrimaryClip(clip)
            }
            ClipboardImageProvider.remember(ctx, secondUri)
            check(ClipboardImageProvider.latestImage(ctx) == secondUri)
            check(firstFile.readBytes().contentEquals(original))
            result.putString("stream", "PASS: unique PNG URI, MIME negotiation, typed image read, file metadata, clipboard image MIME, latest image persistence")
            finish(-1, result)
        } catch (error: Throwable) {
            result.putString("stream", "FAIL: ${error.stackTraceToString()}")
            finish(1, result)
        }
    }
}

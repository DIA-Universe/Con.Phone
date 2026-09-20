package universe.dia.conphone

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import android.content.ClipDescription
import android.content.res.AssetFileDescriptor
import android.os.Bundle

class ClipboardImageProvider : ContentProvider() {
    companion object {
        const val AUTHORITY = "universe.dia.conphone.clipboard"
        val URI: Uri = Uri.parse("content://$AUTHORITY/latest")
        const val FILE_NAME = "conphone-clipboard.png"
        fun imageDirectory(context: android.content.Context) = File(context.filesDir, "received-images").apply { mkdirs() }
        fun newImage(context: android.content.Context): Pair<File, Uri> {
            val name = "${java.util.UUID.randomUUID()}.png"
            return File(imageDirectory(context), name) to Uri.parse("content://$AUTHORITY/images/$name")
        }
        fun latestImage(context: android.content.Context): Uri? = context.getSharedPreferences("received-image", 0)
            .getString("uri", null)?.let(Uri::parse)
        fun remember(context: android.content.Context, uri: Uri) {
            context.getSharedPreferences("received-image", 0).edit().putString("uri", uri.toString()).apply()
        }
        val APK_URI: Uri = Uri.parse("content://$AUTHORITY/updates/companion.apk")
    }

    private fun file(uri: Uri): File {
        val ctx = requireNotNull(context)
        require(uri.scheme == "content" && uri.authority == AUTHORITY)
        if (uri == URI) return File(ctx.cacheDir, FILE_NAME)
        if (uri == APK_URI) return File(ctx.filesDir, "updates/companion.apk")
        val parts = uri.pathSegments
        require(parts.size == 2 && parts[0] == "images" &&
            parts[1].matches(Regex("[0-9a-f-]{36}\\.png"))) { "Unknown URI" }
        return File(imageDirectory(ctx), parts[1])
    }

    override fun onCreate(): Boolean = true

    override fun getType(uri: Uri): String? = runCatching {
        file(uri)
        if (uri == APK_URI) "application/vnd.android.package-archive" else "image/png"
    }.getOrNull()

    override fun getStreamTypes(uri: Uri, mimeTypeFilter: String): Array<String>? =
        getType(uri)?.takeIf { ClipDescription.compareMimeTypes(it, mimeTypeFilter) }?.let { arrayOf(it) }

    override fun openTypedAssetFile(uri: Uri, mimeTypeFilter: String, opts: Bundle?): AssetFileDescriptor {
        require(getStreamTypes(uri, mimeTypeFilter) != null) { "Unsupported MIME type" }
        return AssetFileDescriptor(openFile(uri, "r"), 0, AssetFileDescriptor.UNKNOWN_LENGTH)
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        require(mode == "r") { "Read only" }
        return ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?
    ): Cursor {
        val columns = projection ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        val cursor = MatrixCursor(columns)
        val f = file(uri)
        val row = cursor.newRow()
        columns.forEach {
            when (it) {
                OpenableColumns.DISPLAY_NAME -> row.add(f.name)
                OpenableColumns.SIZE -> row.add(if (f.exists()) f.length() else 0L)
                "mime_type" -> row.add(getType(uri))
                else -> row.add(null)
            }
        }
        return cursor
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
}

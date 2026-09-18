package universe.dia.conphone

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File

class ClipboardImageProvider : ContentProvider() {
    companion object {
        const val AUTHORITY = "universe.dia.conphone.clipboard"
        val URI: Uri = Uri.parse("content://$AUTHORITY/latest")
        const val FILE_NAME = "conphone-clipboard.png"
    }

    private fun file(): File = File(requireNotNull(context).cacheDir, FILE_NAME)

    override fun onCreate(): Boolean = true

    override fun getType(uri: Uri): String? =
        if (uri == URI) "image/png" else null

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        require(uri == URI) { "Unknown URI" }
        require(mode == "r") { "Read only" }
        return ParcelFileDescriptor.open(file(), ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?
    ): Cursor {
        require(uri == URI) { "Unknown URI" }
        val columns = projection ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        val cursor = MatrixCursor(columns)
        val f = file()
        val row = cursor.newRow()
        columns.forEach {
            when (it) {
                OpenableColumns.DISPLAY_NAME -> row.add(FILE_NAME)
                OpenableColumns.SIZE -> row.add(if (f.exists()) f.length() else 0L)
                else -> row.add(null)
            }
        }
        return cursor
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
}

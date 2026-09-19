package universe.dia.conphone

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

class AndroidSharedPayloadStore(private val context: Context) {
    companion object {
        private const val PREFS = "conphone_shared_payloads"
        private const val QUEUE_KEY = "queue_v1"
        private const val DIRECTORY = "SharedPayloads"
        private const val MAX_ITEMS = 8
        private const val MAX_TEXT_BYTES = 1024 * 1024
        private const val MAX_IMAGE_BYTES = 32 * 1024 * 1024
        private const val MAX_TOTAL_BYTES = 40L * 1024 * 1024
        private const val MAX_AGE_MS = 24L * 60 * 60 * 1000
    }

    enum class Kind { TEXT, IMAGE }

    data class Item(
        val id: String,
        val kind: Kind,
        val data: ByteArray,
        val mime: String?
    )

    private data class Meta(
        val id: String,
        val kind: Kind,
        val fileName: String,
        val mime: String?,
        val createdAt: Long,
        val size: Long
    )

    private val gate = Any()
    private val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val directory = File(context.filesDir, DIRECTORY).apply { mkdirs() }

    fun saveText(text: String): String {
        val bytes = text.toByteArray(Charsets.UTF_8)
        require(bytes.isNotEmpty() && bytes.size <= MAX_TEXT_BYTES) {
            "공유 텍스트가 안전 크기 제한을 초과합니다."
        }
        return saveBytes(Kind.TEXT, bytes, "text/plain")
    }

    fun saveImage(uri: Uri, mime: String?): String {
        val id = UUID.randomUUID().toString()
        val fileName = "share-$id.bin"
        val target = safeFile(fileName)
        var total = 0L

        try {
            context.contentResolver.openInputStream(uri).use { input ->
                requireNotNull(input) { "공유 이미지를 열 수 없습니다." }
                FileOutputStream(target).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        total += read
                        require(total <= MAX_IMAGE_BYTES) {
                            "공유 이미지가 32 MiB 제한을 초과합니다."
                        }
                        output.write(buffer, 0, read)
                    }
                }
            }

            require(total > 0) { "공유 이미지가 비어 있습니다." }

            synchronized(gate) {
                val queue = loadAndPruneLocked()
                ensureCapacityLocked(queue, total)
                queue.add(
                    Meta(
                        id = id,
                        kind = Kind.IMAGE,
                        fileName = fileName,
                        mime = mime,
                        createdAt = System.currentTimeMillis(),
                        size = total
                    )
                )
                saveLocked(queue)
            }
            return id
        } catch (ex: Exception) {
            runCatching { target.delete() }
            throw ex
        }
    }

    fun peek(): Item? = synchronized(gate) {
        while (true) {
            val queue = loadAndPruneLocked()
            val meta = queue.firstOrNull() ?: return@synchronized null
            val file = safeFile(meta.fileName)

            val data = runCatching {
                require(file.isFile) { "공유 파일이 없습니다." }
                require(file.length() == meta.size) { "공유 파일 크기가 변경되었습니다." }
                require(
                    meta.size in 1..when (meta.kind) {
                        Kind.TEXT -> MAX_TEXT_BYTES.toLong()
                        Kind.IMAGE -> MAX_IMAGE_BYTES.toLong()
                    }
                ) { "공유 파일 크기가 유효하지 않습니다." }
                file.readBytes()
            }.getOrNull()

            if (data != null) {
                return@synchronized Item(
                    id = meta.id,
                    kind = meta.kind,
                    data = data,
                    mime = meta.mime
                )
            }

            queue.removeAt(0)
            saveLocked(queue)
            runCatching { file.delete() }
        }
        @Suppress("UNREACHABLE_CODE")
        null
    }

    fun clear(id: String) = synchronized(gate) {
        val queue = loadAndPruneLocked()
        val index = queue.indexOfFirst { it.id == id }
        if (index < 0) return@synchronized

        val meta = queue.removeAt(index)
        saveLocked(queue)
        runCatching { safeFile(meta.fileName).delete() }
    }

    fun count(): Int = synchronized(gate) {
        loadAndPruneLocked().size
    }

    private fun saveBytes(kind: Kind, bytes: ByteArray, mime: String?): String {
        val id = UUID.randomUUID().toString()
        val fileName = "share-$id.bin"
        val target = safeFile(fileName)

        try {
            target.writeBytes(bytes)
            synchronized(gate) {
                val queue = loadAndPruneLocked()
                ensureCapacityLocked(queue, bytes.size.toLong())
                queue.add(
                    Meta(
                        id = id,
                        kind = kind,
                        fileName = fileName,
                        mime = mime,
                        createdAt = System.currentTimeMillis(),
                        size = bytes.size.toLong()
                    )
                )
                saveLocked(queue)
            }
            return id
        } catch (ex: Exception) {
            runCatching { target.delete() }
            throw ex
        }
    }

    private fun ensureCapacityLocked(queue: List<Meta>, newBytes: Long) {
        require(queue.size < MAX_ITEMS) {
            "PC 전송 대기열이 가득 찼습니다. Con.Phone 앱을 열어 먼저 전송해 주세요."
        }

        val current = queue.sumOf { it.size }
        require(current + newBytes <= MAX_TOTAL_BYTES) {
            "PC 전송 대기열의 총 크기 제한(40 MiB)을 초과합니다."
        }
    }

    private fun loadAndPruneLocked(): MutableList<Meta> {
        val queue = loadLocked()
        val cutoff = System.currentTimeMillis() - MAX_AGE_MS
        val expired = queue.filter { it.createdAt < cutoff }
        if (expired.isNotEmpty()) {
            queue.removeAll(expired.toSet())
            expired.forEach { runCatching { safeFile(it.fileName).delete() } }
            saveLocked(queue)
        }

        // Clean orphaned files as well.
        val referenced = queue.mapTo(HashSet()) { it.fileName }
        directory.listFiles()?.forEach { file ->
            if (file.name !in referenced &&
                System.currentTimeMillis() - file.lastModified() > MAX_AGE_MS) {
                runCatching { file.delete() }
            }
        }
        return queue
    }

    private fun loadLocked(): MutableList<Meta> {
        val text = prefs.getString(QUEUE_KEY, null) ?: return mutableListOf()
        return runCatching {
            val array = JSONArray(text)
            MutableList(array.length()) { index ->
                val obj = array.getJSONObject(index)
                Meta(
                    id = obj.getString("id"),
                    kind = Kind.valueOf(obj.getString("kind")),
                    fileName = obj.getString("file"),
                    mime = obj.optString("mime").takeIf { it.isNotEmpty() },
                    createdAt = obj.getLong("created"),
                    size = obj.getLong("size")
                )
            }
        }.getOrElse {
            prefs.edit().remove(QUEUE_KEY).apply()
            mutableListOf()
        }
    }

    private fun saveLocked(queue: List<Meta>) {
        if (queue.isEmpty()) {
            prefs.edit().remove(QUEUE_KEY).apply()
            return
        }

        val array = JSONArray()
        queue.forEach { meta ->
            array.put(
                JSONObject()
                    .put("id", meta.id)
                    .put("kind", meta.kind.name)
                    .put("file", meta.fileName)
                    .put("mime", meta.mime ?: "")
                    .put("created", meta.createdAt)
                    .put("size", meta.size)
            )
        }
        prefs.edit().putString(QUEUE_KEY, array.toString()).apply()
    }

    private fun safeFile(fileName: String): File {
        require(fileName.isNotEmpty() && File(fileName).name == fileName) {
            "잘못된 공유 파일 이름입니다."
        }

        val file = File(directory, fileName).canonicalFile
        require(file.parentFile == directory.canonicalFile) {
            "허용되지 않은 공유 파일 경로입니다."
        }
        return file
    }
}

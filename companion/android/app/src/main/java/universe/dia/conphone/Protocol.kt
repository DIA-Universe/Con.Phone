package universe.dia.conphone

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap

enum class ClipboardKind(val code: Int) {
    TEXT(1), PNG(2), JPEG(3), WEBP(4);

    companion object {
        fun fromCode(code: Int): ClipboardKind =
            entries.firstOrNull { it.code == code }
                ?: throw IllegalArgumentException("Unknown clipboard kind: $code")
    }
}

data class ClipboardContent(
    val kind: ClipboardKind,
    val data: ByteArray,
    val hash: ByteArray = sha256(data)
)

data class ClipboardWireChunk(
    val transferId: ByteArray,
    val kind: ClipboardKind,
    val index: Int,
    val count: Int,
    val totalLength: Int,
    val hash: ByteArray,
    val payload: ByteArray
)

data class BleFragment(
    val transferId: UInt,
    val index: Int,
    val count: Int,
    val payload: ByteArray
)

fun sha256(bytes: ByteArray): ByteArray =
    MessageDigest.getInstance("SHA-256").digest(bytes)

object ClipboardProtocol {
    const val MAX_TEXT = 1024 * 1024
    const val MAX_IMAGE = 32 * 1024 * 1024
    const val BLE_HEADER = 10
    const val WIRE_HEADER = 70
    const val MAX_CHUNK = 1024 * 1024
    const val MAX_WIRE_FRAME = WIRE_HEADER + MAX_CHUNK
    private val magic = byteArrayOf('C'.code.toByte(), 'P'.code.toByte(), 'H'.code.toByte(), '1'.code.toByte())
    private val random = SecureRandom()

    fun text(value: String): ClipboardContent {
        val bytes = value.toByteArray(StandardCharsets.UTF_8)
        validate(ClipboardKind.TEXT, bytes)
        return ClipboardContent(ClipboardKind.TEXT, bytes)
    }

    fun image(kind: ClipboardKind, bytes: ByteArray): ClipboardContent {
        require(kind != ClipboardKind.TEXT)
        validate(kind, bytes)
        return ClipboardContent(kind, bytes.copyOf())
    }

    fun validate(kind: ClipboardKind, bytes: ByteArray) {
        require(bytes.isNotEmpty()) { "Clipboard content is empty." }
        val max = if (kind == ClipboardKind.TEXT) MAX_TEXT else MAX_IMAGE
        require(bytes.size <= max) { "Clipboard content exceeds $max bytes." }

        when (kind) {
            ClipboardKind.TEXT -> {
                StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(bytes))
            }
            ClipboardKind.PNG -> require(
                bytes.size >= 8 &&
                    bytes.copyOfRange(0, 8).contentEquals(
                        byteArrayOf(137.toByte(), 80, 78, 71, 13, 10, 26, 10)
                    )
            ) { "Invalid PNG." }
            ClipboardKind.JPEG -> require(
                bytes.size >= 4 &&
                    bytes[0] == 0xFF.toByte() && bytes[1] == 0xD8.toByte() &&
                    bytes[bytes.size - 2] == 0xFF.toByte() && bytes.last() == 0xD9.toByte()
            ) { "Invalid JPEG." }
            ClipboardKind.WEBP -> require(
                bytes.size >= 12 &&
                    String(bytes, 0, 4, Charsets.US_ASCII) == "RIFF" &&
                    String(bytes, 8, 4, Charsets.US_ASCII) == "WEBP"
            ) { "Invalid WebP." }
        }
    }

    fun encode(content: ClipboardContent, chunkSize: Int = 64 * 1024): List<ByteArray> {
        require(chunkSize in 256..MAX_CHUNK)
        validate(content.kind, content.data)

        val transferId = ByteArray(16).also(random::nextBytes)
        val hash = sha256(content.data)
        val count = (content.data.size + chunkSize - 1) / chunkSize

        return (0 until count).map { index ->
            val offset = index * chunkSize
            val length = minOf(chunkSize, content.data.size - offset)
            ByteBuffer.allocate(70 + length)
                .order(ByteOrder.LITTLE_ENDIAN)
                .apply {
                    put(magic)
                    put(1)
                    put(transferId)
                    put(content.kind.code.toByte())
                    putInt(index)
                    putInt(count)
                    putInt(content.data.size)
                    put(hash)
                    putInt(length)
                    put(content.data, offset, length)
                }
                .array()
        }
    }

    fun decode(frame: ByteArray): ClipboardWireChunk {
        require(frame.size in WIRE_HEADER..MAX_WIRE_FRAME) { "Clipboard frame size is invalid." }
        require(frame.copyOfRange(0, 4).contentEquals(magic) && frame[4].toInt() == 1) {
            "Unsupported clipboard frame."
        }

        val b = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN)
        b.position(5)
        val transfer = ByteArray(16).also(b::get)
        val kind = ClipboardKind.fromCode(b.get().toInt() and 0xFF)
        val index = b.int
        val count = b.int
        val total = b.int
        val hash = ByteArray(32).also(b::get)
        val payloadLength = b.int

        val maxTotal = if (kind == ClipboardKind.TEXT) MAX_TEXT else MAX_IMAGE
        val maxCount = (maxTotal + 255) / 256
        require(count in 1..maxCount && index in 0 until count)
        require(total in 1..maxTotal)
        require(payloadLength in 1..MAX_CHUNK && payloadLength <= total && b.remaining() == payloadLength)
        val payload = ByteArray(payloadLength).also(b::get)

        return ClipboardWireChunk(transfer, kind, index, count, total, hash, payload)
    }

    fun splitBle(message: ByteArray, maxGattValue: Int, transferId: UInt): List<ByteArray> {
        require(maxGattValue > BLE_HEADER)
        val payloadSize = maxGattValue - BLE_HEADER
        val count = (message.size + payloadSize - 1) / payloadSize
        require(count in 1..0xFFFF)

        return (0 until count).map { index ->
            val offset = index * payloadSize
            val length = minOf(payloadSize, message.size - offset)
            ByteBuffer.allocate(BLE_HEADER + length)
                .order(ByteOrder.LITTLE_ENDIAN)
                .apply {
                    put(0xC7.toByte())
                    put(1)
                    putInt(transferId.toInt())
                    putShort(index.toShort())
                    putShort(count.toShort())
                    put(message, offset, length)
                }
                .array()
        }
    }

    fun decodeBle(frame: ByteArray): BleFragment {
        require(frame.size >= BLE_HEADER && frame[0] == 0xC7.toByte() && frame[1].toInt() == 1)
        val b = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN)
        b.position(2)
        val id = b.int.toUInt()
        val index = b.short.toInt() and 0xFFFF
        val count = b.short.toInt() and 0xFFFF
        require(count > 0 && index < count)
        val payload = ByteArray(b.remaining()).also(b::get)
        return BleFragment(id, index, count, payload)
    }
}

class ClipboardWireReassembler {
    private data class Pending(
        val kind: ClipboardKind,
        val count: Int,
        val total: Int,
        val hash: ByteArray,
        val parts: MutableMap<Int, ByteArray> = mutableMapOf(),
        val created: Long = System.currentTimeMillis(),
        var bytes: Int = 0
    )

    private val pending = ConcurrentHashMap<String, Pending>()
    private val completed = ConcurrentHashMap<String, Long>()

    fun add(chunk: ClipboardWireChunk): ClipboardContent? {
        cleanup()
        val id = chunk.transferId.joinToString("") { "%02x".format(it) }
        require(!completed.containsKey(id)) { "Clipboard transfer replay rejected." }
        val state = pending.compute(id) { _, current ->
            if (current == null) {
                Pending(chunk.kind, chunk.count, chunk.totalLength, chunk.hash.copyOf())
            } else {
                require(
                    current.kind == chunk.kind &&
                        current.count == chunk.count &&
                        current.total == chunk.totalLength &&
                        current.hash.contentEquals(chunk.hash)
                ) { "Clipboard metadata changed." }
                current
            }
        }!!

        if (!state.parts.containsKey(chunk.index)) {
            require(state.bytes.toLong() + chunk.payload.size <= state.total.toLong()) {
                "Clipboard transfer exceeds declared total length."
            }
            val copy = chunk.payload.copyOf()
            state.parts[chunk.index] = copy
            state.bytes += copy.size
        }
        if (state.parts.size != state.count) return null

        val output = ByteArrayOutputStream(state.total)
        for (i in 0 until state.count) {
            output.write(state.parts[i] ?: return null)
        }

        pending.remove(id)
        val bytes = output.toByteArray()
        require(bytes.size == state.total && sha256(bytes).contentEquals(state.hash)) {
            "Clipboard integrity check failed."
        }
        ClipboardProtocol.validate(state.kind, bytes)
        val content = ClipboardContent(state.kind, bytes)
        completed[id] = System.currentTimeMillis()
        return content
    }

    private fun cleanup() {
        val now = System.currentTimeMillis()
        pending.entries.removeIf { now - it.value.created > 30_000 }
        completed.entries.removeIf { now - it.value > 30_000 }
    }
}

class BleReassembler {
    private data class Pending(
        val count: Int,
        val parts: MutableMap<Int, ByteArray> = mutableMapOf(),
        val created: Long = System.currentTimeMillis(),
        var bytes: Int = 0
    )

    private val pending = ConcurrentHashMap<UInt, Pending>()
    private val completed = ConcurrentHashMap<UInt, Long>()

    fun add(fragment: BleFragment): ByteArray? {
        val now = System.currentTimeMillis()
        pending.entries.removeIf { now - it.value.created > 30_000 }
        completed.entries.removeIf { now - it.value > 30_000 }
        require(!completed.containsKey(fragment.transferId)) {
            "BLE clipboard fragment replay rejected."
        }

        val state = pending.compute(fragment.transferId) { _, current ->
            if (current == null) Pending(fragment.count)
            else {
                require(current.count == fragment.count) { "BLE fragment count changed." }
                current
            }
        }!!

        if (!state.parts.containsKey(fragment.index)) {
            require(state.bytes.toLong() + fragment.payload.size <= ClipboardProtocol.MAX_WIRE_FRAME) {
                "BLE clipboard frame exceeds the wire-frame safety limit."
            }
            val copy = fragment.payload.copyOf()
            state.parts[fragment.index] = copy
            state.bytes += copy.size
        }
        if (state.parts.size != state.count) return null

        val output = ByteArrayOutputStream()
        for (i in 0 until state.count) output.write(state.parts[i] ?: return null)
        pending.remove(fragment.transferId)
        completed[fragment.transferId] = now
        return output.toByteArray()
    }
}

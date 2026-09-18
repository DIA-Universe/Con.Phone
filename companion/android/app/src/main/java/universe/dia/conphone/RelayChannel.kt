package universe.dia.conphone

import android.util.Base64
import okhttp3.*
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class RelayChannel(
    endpoint: String,
    private val deviceHash: ByteArray,
    private val secret: ByteArray,
    private val onFrame: (ByteArray) -> Unit,
    private val onStatus: (String) -> Unit,
    private val onClosed: () -> Unit
) {
    private val client = OkHttpClient.Builder()
        .pingInterval(20, TimeUnit.SECONDS)
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    private val random = SecureRandom()
    private val aad = "ConPhone-Clipboard-Relay-v1".toByteArray(Charsets.US_ASCII) + deviceHash
    private val url = buildUrl(endpoint)
    @Volatile private var socket: WebSocket? = null
    @Volatile var ready: Boolean = false
        private set

    fun start() {
        if (socket != null) return
        onStatus("Clipboard Relay 연결 중")
        socket = client.newWebSocket(
            Request.Builder().url(url).build(),
            object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    ready = true
                    onStatus("Clipboard 인터넷 Relay 연결됨 · 서버는 암호문만 중계")
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    runCatching { decrypt(bytes.toByteArray()) }
                        .onSuccess(onFrame)
                        .onFailure { onStatus("Relay Clipboard 수신 무결성 오류") }
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    ready = false
                    webSocket.close(code, reason)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    ready = false
                    socket = null
                    onClosed()
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    ready = false
                    socket = null
                    onStatus("Clipboard Relay 연결 실패: ${t.message ?: t.javaClass.simpleName}")
                    onClosed()
                }
            }
        )
    }

    fun sendFrames(frames: List<ByteArray>): Boolean {
        val ws = socket ?: return false
        if (!ready) return false

        for (frame in frames) {
            if (!ws.send(encrypt(frame).toByteString()))
                return false
        }
        onStatus("Clipboard 전송 완료 · Relay(E2E)")
        return true
    }

    fun close() {
        ready = false
        socket?.close(1000, "disabled")
        socket = null
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }

    private fun encrypt(plaintext: ByteArray): ByteArray {
        require(plaintext.isNotEmpty() && plaintext.size <= 2 * 1024 * 1024)
        val nonce = ByteArray(12).also(random::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(
            Cipher.ENCRYPT_MODE,
            SecretKeySpec(secret, "AES"),
            GCMParameterSpec(128, nonce)
        )
        cipher.updateAAD(aad)
        val encrypted = cipher.doFinal(plaintext)
        return byteArrayOf(1) + nonce + encrypted
    }

    private fun decrypt(envelope: ByteArray): ByteArray {
        require(envelope.size >= 30 && envelope[0].toInt() == 1)
        val nonce = envelope.copyOfRange(1, 13)
        val encrypted = envelope.copyOfRange(13, envelope.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(
            Cipher.DECRYPT_MODE,
            SecretKeySpec(secret, "AES"),
            GCMParameterSpec(128, nonce)
        )
        cipher.updateAAD(aad)
        return cipher.doFinal(encrypted)
    }

    private fun buildUrl(endpoint: String): String {
        val base = endpoint.trim().trimEnd('/')
        val wsBase = when {
            base.startsWith("https://", ignoreCase = true) -> "wss://" + base.substring(8)
            base.startsWith("http://", ignoreCase = true) -> "ws://" + base.substring(7)
            base.startsWith("wss://", ignoreCase = true) || base.startsWith("ws://", ignoreCase = true) -> base
            else -> error("Unsupported relay URL")
        }

        val room = deriveToken("ConPhone-Relay-Room-v1")
        val auth = deriveToken("ConPhone-Relay-Auth-v1")
        return "$wsBase/relay?room=$room&auth=$auth&side=phone"
    }

    private fun deriveToken(label: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(secret, "HmacSHA256"))
        val value = mac.doFinal(label.toByteArray(Charsets.US_ASCII) + deviceHash)
        return Base64.encodeToString(
            value,
            Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING
        )
    }
}

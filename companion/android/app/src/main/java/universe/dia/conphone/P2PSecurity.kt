package universe.dia.conphone

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object P2PSecurity {
    private val sessionLabel =
        "ConPhone-P2P-Session-v2".toByteArray(Charsets.US_ASCII)

    fun deriveSessionKey(
        secret: ByteArray,
        deviceHash: ByteArray,
        clientNonce: ByteArray,
        serverNonce: ByteArray
    ): ByteArray {
        require(secret.size == 32)
        require(deviceHash.size == 32)
        require(clientNonce.size == 16)
        require(serverNonce.size == 16)

        val material =
            sessionLabel +
            deviceHash +
            clientNonce +
            serverNonce

        return try {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(secret, "HmacSHA256"))
            mac.doFinal(material)
        } finally {
            material.fill(0)
        }
    }
}

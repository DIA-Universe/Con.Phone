package universe.dia.conphone

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureCredentialStore(context: Context) {
    private val prefs = context.getSharedPreferences("conphone_secure", Context.MODE_PRIVATE)

    companion object {
        private const val KEYSTORE = "AndroidKeyStore"
        private const val KEY_ALIAS = "ConPhoneClipboardCredentialKeyV1"
        private const val PREF_CREDENTIAL = "credential_v1"
        private const val PREF_RELAY = "relay_endpoint_v1"
    }

    fun saveCredential(deviceHash: ByteArray, secret: ByteArray) {
        require(deviceHash.size == 32 && secret.size == 32)

        val plaintext = deviceHash + secret
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encrypted = cipher.doFinal(plaintext)

        val packed = byteArrayOf(cipher.iv.size.toByte()) + cipher.iv + encrypted
        prefs.edit()
            .putString(
                PREF_CREDENTIAL,
                Base64.encodeToString(packed, Base64.NO_WRAP)
            )
            .apply()
    }

    fun loadCredential(): Pair<ByteArray, ByteArray>? {
        val encoded = prefs.getString(PREF_CREDENTIAL, null) ?: return null

        return try {
            val packed = Base64.decode(encoded, Base64.NO_WRAP)
            if (packed.size < 1 + 12 + 16) return null

            val ivLength = packed[0].toInt() and 0xFF
            if (ivLength !in 12..16 || packed.size <= 1 + ivLength) return null

            val iv = packed.copyOfRange(1, 1 + ivLength)
            val encrypted = packed.copyOfRange(1 + ivLength, packed.size)

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.DECRYPT_MODE,
                getOrCreateKey(),
                GCMParameterSpec(128, iv)
            )
            val plaintext = cipher.doFinal(encrypted)
            if (plaintext.size != 64) return null

            plaintext.copyOfRange(0, 32) to plaintext.copyOfRange(32, 64)
        } catch (_: Exception) {
            clearCredential()
            null
        }
    }

    fun saveRelayEndpoint(endpoint: String?) {
        prefs.edit().apply {
            if (endpoint.isNullOrBlank()) remove(PREF_RELAY)
            else putString(PREF_RELAY, endpoint.trim())
        }.apply()
    }

    fun loadRelayEndpoint(): String? =
        prefs.getString(PREF_RELAY, null)?.trim()?.takeIf { it.isNotEmpty() }

    fun clearCredential() {
        prefs.edit().remove(PREF_CREDENTIAL).remove(PREF_RELAY).apply()
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }

        val generator = KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_AES,
            KEYSTORE
        )
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return generator.generateKey()
    }
}

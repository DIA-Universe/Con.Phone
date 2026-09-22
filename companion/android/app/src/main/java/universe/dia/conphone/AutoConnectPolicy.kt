package universe.dia.conphone

/** Automatic reconnect is restricted to the user's saved, still-bonded PC. */
object AutoConnectPolicy {
    fun canConnect(
        candidateAddress: String,
        selectedAddress: String?,
        bonded: Boolean,
        connectionExists: Boolean,
        rejected: Boolean = false
    ): Boolean = !connectionExists && !rejected && bonded &&
        !selectedAddress.isNullOrBlank() &&
        candidateAddress.equals(selectedAddress, ignoreCase = true)
}

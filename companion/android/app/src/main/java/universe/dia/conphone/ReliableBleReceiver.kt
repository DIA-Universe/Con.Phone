package universe.dia.conphone

/** Stop-and-wait delivery. Only the last accepted packet can be retried.
 * Cache the application ACK as well, so its loss never reapplies the image. */
class ReliableBleReceiver {
    data class Result(val receipt: ByteArray, val applicationAck: ByteArray?)
    private var previous: ByteArray? = null
    private var previousResult: Result? = null

    fun receive(packet: ByteArray, consume: (ByteArray) -> ByteArray?): Result {
        require(packet.size >= 10 && packet[0] == 0xC7.toByte() && packet[1] == 2.toByte())
        if (previous?.contentEquals(packet) == true) return previousResult!!
        val legacy = packet.copyOf().also { it[1] = 1 }
        // Decode before acknowledging, including count/index/size validation.
        ClipboardProtocol.decodeBle(legacy)
        val result = Result(packet.copyOfRange(0, 8), consume(legacy))
        previous = packet.copyOf()
        previousResult = result
        return result
    }
}

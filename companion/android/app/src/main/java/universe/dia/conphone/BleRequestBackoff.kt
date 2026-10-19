package universe.dia.conphone

/** A rejected API start submitted no ATT work: retry without closing the shared HID link. */
internal class BleRequestBackoff {
    private var failures = 0
    fun rejected(): Long = minOf(250L shl failures.coerceAtMost(5), 5_000L).also {
        failures = (failures + 1).coerceAtMost(5)
    }
    fun accepted() { failures = 0 }
}

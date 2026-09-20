package universe.dia.conphone

/** Tracks the currently observed clipboard, rather than remembering a value forever. */
class FocusedTextSync {
    private var queued: String? = null
    fun capture(text: String?, remote: Boolean, manual: Boolean, enqueue: (String) -> Unit): Boolean {
        if (text.isNullOrEmpty() || (remote && !manual)) {
            queued = null
            return false
        }
        if (!manual && text == queued) return false
        enqueue(text) // A failed durable write must remain retryable.
        queued = text
        return true
    }
}

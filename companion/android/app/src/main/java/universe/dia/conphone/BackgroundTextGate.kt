package universe.dia.conphone

/** Track copy events, not just text: copying the same value later is a new event. */
class BackgroundTextGate {
    private var last: Pair<Long, String?>? = null
    fun capture(timestamp: Long, text: String?, remote: Boolean, enqueue: (String) -> Unit): Boolean {
        val current = timestamp to text
        if (current == last) return false
        if (text.isNullOrEmpty() || remote) { last = current; return false }
        enqueue(text)
        last = current
        return true
    }
}

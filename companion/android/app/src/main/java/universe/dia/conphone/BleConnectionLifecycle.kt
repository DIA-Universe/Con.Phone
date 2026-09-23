package universe.dia.conphone

/** All methods/callbacks run on the service's main Handler. No sleeping or OS autoConnect. */
enum class BleConnectionState {
    DISCONNECTED, SCANNING, CONNECTING, DISCOVERING_SERVICES, WAITING_AUTHORIZATION, CONNECTED,
    DISCONNECTING, RECONNECT_WAIT, RECOVERY_PAUSED
}

internal fun interface BleTimer { fun cancel() }

internal class BleConnectionLifecycle<G : Any>(
    private val schedule: (Long, () -> Unit) -> BleTimer,
    private val disableNotifications: (G, Boolean) -> Boolean,
    private val disconnect: (G) -> Unit,
    private val close: (G) -> Unit,
    private val resetTransport: () -> Unit,
    private val reconnect: () -> Unit,
    private val log: (String) -> Unit,
    private val recoveryPaused: () -> Unit = {}
) {
    companion object {
        const val BLE_CONNECT_TIMEOUT_MS = 15_000L
        const val BLE_OPERATION_TIMEOUT_MS = 10_000L
        const val BLE_CLEANUP_TIMEOUT_MS = 750L
        const val BLE_RECONNECT_MAX_MS = 30_000L
        const val BLE_RECONNECT_LIMIT = 5
        const val BLE_STABLE_CONNECTION_MS = 60_000L
    }

    var state = BleConnectionState.DISCONNECTED
        private set
    var generation = 0L
        private set
    var resource: G? = null
        private set
    var desired = true
        private set
    private var radioOn = true
    private var stopped = false
    private var retries = 0
    private var retryAfterCleanup = false
    private var connectTimer: BleTimer? = null
    private var operationTimer: BleTimer? = null
    private var cleanupTimer: BleTimer? = null
    private var retryTimer: BleTimer? = null
    private var stableTimer: BleTimer? = null
    private var operation: String? = null
    private var operationSequence = 0L
    val idle get() = operation == null && state in setOf(
        BleConnectionState.DISCOVERING_SERVICES, BleConnectionState.WAITING_AUTHORIZATION, BleConnectionState.CONNECTED)

    fun connect(open: (Long) -> G?): Boolean {
        if (stopped || !desired || !radioOn || state != BleConnectionState.DISCONNECTED) {
            log("[BLE][Session $generation] connect ignored: $state")
            return false
        }
        check(resource == null) // The only path back here passes through close.
        generation++
        transition(BleConnectionState.CONNECTING)
        val session = generation
        try {
            resource = open(session) ?: error("connectGatt returned null")
            connectTimer = schedule(BLE_CONNECT_TIMEOUT_MS) {
                if (generation == session && state in setOf(BleConnectionState.CONNECTING, BleConnectionState.DISCOVERING_SERVICES))
                    cleanup("connection timeout", retry = true)
            }
        } catch (ex: Exception) {
            log("[BLE][Session $session] connect exception=${ex.javaClass.simpleName}")
            cleanup("connect failed", retry = true)
        }
        return true
    }

    fun accepts(g: G, session: Long): Boolean =
        resource === g && generation == session && !stopped && state in setOf(
            BleConnectionState.CONNECTING, BleConnectionState.DISCOVERING_SERVICES,
            BleConnectionState.WAITING_AUTHORIZATION, BleConnectionState.CONNECTED)

    fun linkConnected() {
        if (state == BleConnectionState.CONNECTING) transition(BleConnectionState.DISCOVERING_SERVICES)
    }

    fun servicesConfigured() {
        if (state != BleConnectionState.DISCOVERING_SERVICES) return
        connectTimer?.cancel(); connectTimer = null
        transition(BleConnectionState.WAITING_AUTHORIZATION)
    }

    fun ready() {
        if (state !in setOf(BleConnectionState.DISCOVERING_SERVICES, BleConnectionState.WAITING_AUTHORIZATION)) return
        connectTimer?.cancel(); connectTimer = null
        transition(BleConnectionState.CONNECTED)
        // Brief successes must not turn a flapping connection into an endless 1-second loop.
        val session = generation
        stableTimer = schedule(BLE_STABLE_CONNECTION_MS) {
            if (generation == session && state == BleConnectionState.CONNECTED) retries = 0
        }
    }

    fun beginOperation(name: String): Boolean {
        if (!idle) return false
        operation = name
        val session = generation
        val sequence = ++operationSequence
        operationTimer = schedule(BLE_OPERATION_TIMEOUT_MS) {
            if (generation == session && operationSequence == sequence && operation == name)
                cleanup("operation timeout: $name", retry = true)
        }
        return true
    }

    fun finishOperation(name: String): Boolean {
        if (operation != name || !acceptsCurrent()) return false
        operationTimer?.cancel(); operationTimer = null
        operation = null
        return true
    }

    private fun acceptsCurrent() = resource?.let { accepts(it, generation) } == true

    fun resume() {
        if (stopped) return
        desired = true
        // Manual requests may bypass backoff, but never interrupt an active attempt.
        if (state in setOf(BleConnectionState.DISCONNECTED, BleConnectionState.RECONNECT_WAIT, BleConnectionState.RECOVERY_PAUSED)) {
            retryTimer?.cancel(); retryTimer = null
            retries = 0
            transition(BleConnectionState.DISCONNECTED)
        }
    }

    fun userDisconnect() {
        desired = false
        cleanup("user disconnect", retry = false, graceful = true)
    }

    fun radioChanged(on: Boolean) {
        val wasOn = radioOn
        radioOn = on
        if (!on) cleanup("Bluetooth OFF", retry = false)
        else if (!wasOn && desired && !stopped && state == BleConnectionState.DISCONNECTED) {
            retries = 0
            scheduleReconnect()
        }
    }

    fun shutdown() {
        stopped = true
        desired = false
        cleanup("service shutdown", retry = false)
    }

    /** CCCD is best effort and bounded; disconnect and close execute even if either throws. */
    fun cleanup(reason: String, retry: Boolean, graceful: Boolean = false) {
        retryTimer?.cancel(); retryTimer = null
        stableTimer?.cancel(); stableTimer = null
        retryAfterCleanup = retry && desired && radioOn && !stopped
        if (state == BleConnectionState.DISCONNECTING) {
            if (!graceful) finishCleanup(resource, generation)
            return
        }
        val canWriteCccd = graceful && idle
        connectTimer?.cancel(); connectTimer = null
        operationTimer?.cancel(); operationTimer = null
        operation = null
        ++operationSequence
        transition(BleConnectionState.DISCONNECTING)
        log("[BLE][Session $generation] cleanup started: $reason")
        val old = resource
        var awaitingCccd = false
        try {
            if (old != null) awaitingCccd = disableNotifications(old, canWriteCccd)
        } catch (ex: Exception) {
            log("[BLE][Session $generation] notify OFF exception=${ex.javaClass.simpleName}")
        } finally {
            try { resetTransport() }
            catch (ex: Exception) { log("[BLE][Session $generation] reset exception=${ex.javaClass.simpleName}") }
        }
        if (awaitingCccd && graceful) {
            val session = generation
            cleanupTimer = schedule(BLE_CLEANUP_TIMEOUT_MS) { finishCleanup(old, session) }
        } else finishCleanup(old, generation)
    }

    fun finishCleanup(old: G?, session: Long) {
        if (state != BleConnectionState.DISCONNECTING || resource !== old || generation != session) return
        cleanupTimer?.cancel(); cleanupTimer = null
        resource = null // Invalidate BEFORE calling the platform; reentrant callbacks cannot close twice.
        if (old != null) {
            try { disconnect(old) }
            catch (ex: Exception) { log("[BLE][Session $session] disconnect exception=${ex.javaClass.simpleName}") }
            finally {
                try { close(old); log("[BLE][Session $session] GATT closed") }
                catch (ex: Exception) { log("[BLE][Session $session] close exception=${ex.javaClass.simpleName}") }
            }
        }
        transition(BleConnectionState.DISCONNECTED)
        log("[BLE][Session $session] cleanup completed")
        if (retryAfterCleanup) scheduleReconnect()
    }

    private fun scheduleReconnect() {
        if (!desired || !radioOn || stopped || retryTimer != null) return
        if (retries >= BLE_RECONNECT_LIMIT) {
            transition(BleConnectionState.RECOVERY_PAUSED)
            recoveryPaused()
            return
        }
        val delay = minOf(1000L shl retries, BLE_RECONNECT_MAX_MS)
        retries++
        val session = generation
        transition(BleConnectionState.RECONNECT_WAIT)
        log("[BLE][Session $session] reconnect scheduled: ${delay}ms")
        retryTimer = schedule(delay) {
            retryTimer = null
            if (generation == session && state == BleConnectionState.RECONNECT_WAIT && desired && radioOn && !stopped) {
                transition(BleConnectionState.DISCONNECTED)
                reconnect()
            }
        }
    }

    private fun transition(next: BleConnectionState) {
        state = next
        log("[BLE][Session $generation] state=$next")
    }
}

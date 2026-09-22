package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class BleConnectionLifecycleTest {
    private class Gatt { var disconnects = 0; var closes = 0 }
    private class Harness {
        data class Task(val at: Long, val action: () -> Unit, var cancelled: Boolean = false)
        var now = 0L
        val tasks = mutableListOf<Task>()
        val links = mutableListOf<Gatt>()
        val logs = mutableListOf<String>()
        var cccdStarted = false
        var cccdThrows = false
        var disconnectThrows = false
        var resets = 0
        val owner: BleConnectionLifecycle<Gatt> = BleConnectionLifecycle(
            schedule = { delay, action ->
                val task = Task(now + delay, action); tasks.add(task)
                BleTimer { task.cancelled = true }
            },
            disableNotifications = { _, canWrite ->
                if (cccdThrows) error("CCCD disconnected")
                canWrite && cccdStarted
            },
            disconnect = { it.disconnects++; if (disconnectThrows) error("radio gone") },
            close = { it.closes++ },
            resetTransport = { resets++ },
            reconnect = { connect() },
            log = { logs.add(it) }
        )
        fun connect(): Boolean = owner.connect { Gatt().also(links::add) }
        fun connected() { connect(); owner.linkConnected(); owner.ready() }
        fun advance(ms: Long) {
            val end = now + ms
            while (true) {
                val next = tasks.filter { !it.cancelled && it.at <= end }.minByOrNull { it.at } ?: break
                next.cancelled = true; now = next.at; next.action()
            }
            now = end
        }
    }

    @Test fun tenClicksAndConnectingRequestOpenOnlyOneGatt() {
        val h = Harness()
        repeat(10) { h.connect() }
        assertEquals(1, h.links.size)
        h.owner.linkConnected(); assertFalse(h.connect())
        h.owner.ready(); assertFalse(h.connect())
        h.advance(20_000)
        assertEquals(0, h.links.single().closes)
    }

    @Test fun connectionTimeoutClosesBeforeBackoffConnects() {
        val h = Harness(); h.connect()
        h.advance(BleConnectionLifecycle.BLE_CONNECT_TIMEOUT_MS)
        assertEquals(1, h.links.single().closes)
        assertEquals(BleConnectionState.RECONNECT_WAIT, h.owner.state)
        h.advance(999); assertEquals(1, h.links.size)
        h.advance(1); assertEquals(2, h.links.size)
    }

    @Test fun twentyCyclesCloseExactlyOnceAndIgnoreOldCallbacks() {
        val h = Harness()
        repeat(20) {
            h.owner.resume(); h.connected()
            val old = h.links.last(); val session = h.owner.generation
            h.owner.userDisconnect(); h.owner.userDisconnect()
            h.owner.finishCleanup(old, session)
            assertFalse(h.owner.accepts(old, session))
            assertEquals(1, old.closes); assertEquals(1, old.disconnects)
        }
        h.owner.resume(); h.connected()
        h.owner.finishCleanup(h.links.first(), 1)
        assertEquals(BleConnectionState.CONNECTED, h.owner.state)
    }

    @Test fun notificationErrorAndDisconnectErrorNeverPreventClose() {
        val h = Harness(); h.connected(); h.cccdThrows = true; h.disconnectThrows = true
        h.owner.userDisconnect()
        assertEquals(1, h.links.single().closes)
        assertNull(h.owner.resource)
    }

    @Test fun cccdDisableIsBoundedAndDuplicateCleanupCannotCloseAgain() {
        val h = Harness(); h.connected(); h.cccdStarted = true
        val old = h.links.single(); val id = h.owner.generation
        h.owner.userDisconnect()
        assertEquals(BleConnectionState.DISCONNECTING, h.owner.state)
        assertFalse(h.connect())
        h.advance(750)
        h.owner.finishCleanup(old, id)
        assertEquals(1, old.closes)
    }

    @Test fun successfulCccdCallbackClosesWithoutWaitingForTimeout() {
        val h = Harness(); h.connected(); h.cccdStarted = true
        val old = h.links.single(); val id = h.owner.generation
        h.owner.userDisconnect(); h.owner.finishCleanup(old, id)
        h.advance(750)
        assertEquals(1, old.closes)
    }

    @Test fun mtuDiscoveryCccdAndWriteNeverOverlap() {
        val h = Harness(); h.connect(); h.owner.linkConnected()
        for (op in listOf("mtu", "discovery", "cccd", "read:secret", "write")) {
            assertTrue(h.owner.beginOperation(op))
            assertFalse(h.owner.beginOperation("other"))
            assertFalse(h.owner.finishOperation("wrong callback"))
            assertTrue(h.owner.finishOperation(op))
        }
        h.owner.ready(); h.advance(20_000)
        assertEquals(BleConnectionState.CONNECTED, h.owner.state)
    }

    @Test fun everyPendingOperationTimeoutClosesAndCancelsTransfer() {
        for (op in listOf("mtu", "discovery", "cccd", "read:secret", "write")) {
            val h = Harness(); h.connected(); h.owner.beginOperation(op)
            h.advance(BleConnectionLifecycle.BLE_OPERATION_TIMEOUT_MS)
            assertEquals(op, 1, h.links.single().closes)
            assertEquals(op, 1, h.resets)
            assertFalse(h.owner.finishOperation(op))
        }
    }

    @Test fun reconnectBackoffCapsAndUserDisconnectCancelsIt() {
        val h = Harness(); h.connect()
        for (delay in listOf(1000L, 2000L, 4000L, 8000L, 10000L, 10000L)) {
            val count = h.links.size
            h.owner.cleanup("status=133", retry = true)
            h.advance(delay - 1); assertEquals(count, h.links.size)
            h.advance(1); assertEquals(count + 1, h.links.size)
        }
        h.owner.cleanup("status=8", retry = true)
        h.owner.userDisconnect(); h.advance(60_000)
        assertEquals(7, h.links.size)
    }

    @Test fun radioOffClosesAndOnRestoresUnlessUserStopped() {
        val h = Harness(); h.connected(); h.owner.radioChanged(false)
        h.advance(60_000); assertEquals(1, h.links.size)
        h.owner.radioChanged(true); h.advance(1000); assertEquals(2, h.links.size)
        h.owner.userDisconnect(); h.owner.radioChanged(false); h.owner.radioChanged(true)
        h.advance(60_000); assertEquals(2, h.links.size)
    }

    @Test fun shutdownDuringCccdCleanupIsImmediateAndNeverReconnects() {
        val h = Harness(); h.connected(); h.cccdStarted = true
        h.owner.cleanup("switch", retry = true, graceful = true)
        h.owner.shutdown(); h.owner.shutdown(); h.advance(60_000)
        assertEquals(1, h.links.size); assertEquals(1, h.links.single().closes)
        assertFalse(h.connect())
    }

    @Test fun openExceptionLeavesNoOwnedGattAndSchedulesRetry() {
        val h = Harness()
        h.owner.connect { error("adapter off race") }
        assertNull(h.owner.resource)
        h.advance(1000); assertEquals(1, h.links.size)
    }
}

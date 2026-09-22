package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class AutoConnectPolicyTest {
    private val pc = "AA:BB:CC:DD:EE:01"

    @Test fun nearbyKeyboardsMiceAndOtherBondedDevicesNeverBecomeTargets() {
        val nearby = listOf("keyboard", "mouse", "another phone", "other laptop", pc)
        repeat(100) {
            val attempted = nearby.filter { AutoConnectPolicy.canConnect(it, pc, true, false) }
            assertEquals(listOf(pc), attempted)
        }
    }

    @Test fun freshInstallWaitsForPcSelectionInsteadOfConnectingToNearbyHid() {
        for (selection in listOf(null, "", " "))
            assertFalse(AutoConnectPolicy.canConnect(pc, selection, true, false))
    }

    @Test fun removedPairingDoesNotCauseAutomaticPairingRequests() {
        assertFalse(AutoConnectPolicy.canConnect(pc, pc, false, false))
        assertTrue(AutoConnectPolicy.canConnect(pc, pc, true, false))
    }

    @Test fun liveConnectionOrConnectionAttemptPreventsDuplicateConnects() {
        assertFalse(AutoConnectPolicy.canConnect(pc, pc, true, true))
        assertFalse(AutoConnectPolicy.canConnect("other laptop", pc, true, true))
    }

    @Test fun changingSelectionDisablesOldPeerAndAcceptsNewBondedPc() {
        val other = "AA:BB:CC:DD:EE:02"
        assertFalse(AutoConnectPolicy.canConnect(pc, other, true, false))
        assertTrue(AutoConnectPolicy.canConnect(other.lowercase(), other, true, false))
    }

    @Test fun unsupportedSelectedDeviceDoesNotEnterAnAutomaticReconnectLoop() {
        assertFalse(AutoConnectPolicy.canConnect(pc, pc, true, false, rejected = true))
        assertTrue(AutoConnectPolicy.canConnect(pc, pc, true, false, rejected = false))
    }
}

package universe.dia.conphone

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.*
import android.content.*
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiNetworkSpecifier
import android.os.*
import android.widget.Toast
import android.util.Base64
import java.io.*
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class ClipboardBridgeService : Service() {
    companion object {
        @Volatile var lastReceivedText: String? = null
            private set
        @Volatile var latestStatus = "연결 서비스를 시작해 주세요."
            private set
        const val ACTION_CONNECT_DEVICE = "universe.dia.conphone.CONNECT_DEVICE"
        const val EXTRA_DEVICE_ADDRESS = "device_address"
        const val CONNECTION_PREFS = "conphone_connection"
        const val SAVED_PC_ADDRESS = "pc_address"
        const val ACTION_START = "universe.dia.conphone.START"
        const val ACTION_SEND = "universe.dia.conphone.SEND"
        const val ACTION_SEND_SHARED_TEXT = "universe.dia.conphone.SEND_SHARED_TEXT"
        const val ACTION_SEND_SHARED_IMAGE = "universe.dia.conphone.SEND_SHARED_IMAGE"
        const val ACTION_DRAIN_SHARED = "universe.dia.conphone.DRAIN_SHARED"
        const val ACTION_RETRY_P2P = "universe.dia.conphone.RETRY_P2P"
        const val ACTION_STOP = "universe.dia.conphone.STOP"
        const val ACTION_DISCONNECT = "universe.dia.conphone.DISCONNECT"
        const val ACTION_CONNECT = "universe.dia.conphone.CONNECT"
        private const val USER_DISCONNECTED = "user_disconnected"

        const val EXTRA_SHARED_TEXT = "shared_text"
        const val EXTRA_SHARED_IMAGE_PATH = "shared_image_path"
        const val EXTRA_SHARED_IMAGE_MIME = "shared_image_mime"

        private const val CHANNEL = "conphone_bridge"
        private const val NOTIFICATION_ID = 7310
        private const val MAX_PENDING_ITEMS = 8
        private const val MAX_PENDING_BYTES = 40L * 1024 * 1024
        private const val PENDING_TTL_MS = 60_000L

        private val SERVICE_UUID = UUID.fromString("67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val TX_UUID = UUID.fromString("67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val RX_UUID = UUID.fromString("67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val SECRET_UUID = UUID.fromString("67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val CAPABILITIES_UUID = UUID.fromString("67A7F005-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val DIRECT_LINK_UUID = UUID.fromString("67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val CCCD_UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
    }

    @Volatile private var destroyed = false
    private var handshakeComplete = false
    private var writeGeneration = 0L
    private var readingGatt: BluetoothGatt? = null
    private var readGeneration = 0L
    private val readStartBackoff = BleRequestBackoff()
    private val writeStartBackoff = BleRequestBackoff()
    private var deferredBleWrite: Pair<ByteArray, Boolean>? = null
    private var writeRetryPending = false
    private val pendingReads = java.util.ArrayDeque<Pair<BluetoothGatt, BluetoothGattCharacteristic>>()

    private fun readCharacteristic(g: BluetoothGatt, c: BluetoothGattCharacteristic) {
        main.post {
            if (destroyed || gatt !== g) return@post
            if (pendingReads.none { it.first === g && it.second.uuid == c.uuid })
                pendingReads.add(g to c)
            drainReads()
        }
    }

    private fun drainReads() {
        if (destroyed || !bleLifecycle.idle || readingGatt != null || bleWriting) return
        val item = pendingReads.poll() ?: run { writeNextBle(); return }
        val (g, c) = item
        if (gatt !== g) { drainReads(); return }
        readingGatt = g
        val generation = ++readGeneration
        if (!bleLifecycle.beginOperation("read:${c.uuid}")) { readingGatt = null; pendingReads.addFirst(item); return }
        if (!runCatching { g.readCharacteristic(c) }.getOrDefault(false)) {
            bleLifecycle.finishOperation("read:${c.uuid}")
            // false means Android did not start the read; the shared GATT
            // transport may simply be busy. Do not tear down a healthy HID
            // connection on the first rejected periodic capabilities poll.
            pendingReads.addFirst(item)
            val delay = readStartBackoff.rejected()
            bleLog("read not started; keeping link, retry in ${delay}ms")
            main.postDelayed({
                if (!destroyed && gatt === g && readingGatt === g && readGeneration == generation) {
                    readingGatt = null
                    drainReads()
                }
            }, delay)
            return
        }
        readStartBackoff.accepted()
        // The lifecycle owns the single timeout for an operation actually submitted to Android.
    }

    private fun readCompleted(g: BluetoothGatt, c: BluetoothGattCharacteristic): Boolean {
        if (readingGatt === g && bleLifecycle.finishOperation("read:${c.uuid}")) {
            readingGatt = null
            ++readGeneration
            main.post { drainReads() }
            return true
        }
        return false
    }

    private fun pauseGattChannel(g: BluetoothGatt, message: String) {
        if (destroyed || gatt !== g) return
        bleLifecycle.pauseChannel(message)
        showStatus("$message · Bluetooth 연결 유지. 클립보드를 다시 시도하려면 연결을 눌러 주세요.")
    }

    private fun discoverGatt(g: BluetoothGatt) {
        if (destroyed || gatt !== g || !bleLifecycle.beginOperation("discovery")) return
        bleLog("discovering services")
        if (!runCatching { g.discoverServices() }.getOrDefault(false))
            pauseGattChannel(g, "PC 클립보드 서비스 검색 시작 실패")
    }

    private val main = Handler(Looper.getMainLooper())
    private fun bleLog(message: String) = recordBleDiagnostic("[BLE][Session ${bleLifecycle.generation}] $message")
    private fun recordBleDiagnostic(message: String) {
        android.util.Log.i("LINKO-BLE", message)
        ConnectionDiagnostics.write(this, message)
    }
    private val bleLifecycle: BleConnectionLifecycle<BluetoothGatt> = BleConnectionLifecycle(
        schedule = { delay, action ->
            val task = Runnable { action() }
            main.postDelayed(task, delay)
            BleTimer { main.removeCallbacks(task) }
        },
        disableNotifications = { g, canWrite ->
            val characteristic = tx
            if (characteristic != null) runCatching { g.setCharacteristicNotification(characteristic, false) }
            val descriptor = characteristic?.getDescriptor(CCCD_UUID)
            if (canWrite && descriptor != null) {
                bleLog("CCCD disable requested")
                writeCccd(g, descriptor, BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE)
            } else {
                bleLog("CCCD disable skipped: link unavailable or operation pending")
                false
            }
        },
        disconnect = { it.disconnect() },
        close = { it.close() },
        resetTransport = { resetBleTransport() },
        reconnect = { ensureSelectedPcConnection() },
        log = { recordBleDiagnostic(it) },
        recoveryPaused = { showStatus("반복 연결 실패 · 자동 재연결을 멈췄어요. PC의 LINKO 상태를 확인한 뒤 연결을 눌러 주세요.") },
        channelDelayed = { showStatus("클립보드 응답 지연 · Bluetooth 연결 유지. 응답을 기다리거나 연결을 눌러 다시 시도하세요.") }
    )

    @Suppress("DEPRECATION")
    private fun writeCccd(g: BluetoothGatt, descriptor: BluetoothGattDescriptor, value: ByteArray): Boolean =
        if (Build.VERSION.SDK_INT >= 33) g.writeDescriptor(descriptor, value) == BluetoothStatusCodes.SUCCESS
        else { descriptor.value = value; g.writeDescriptor(descriptor) }

    private fun resetBleTransport() {
        gatt = null
        handshakeComplete = false
        readingGatt = null
        readStartBackoff.accepted()
        writeStartBackoff.accepted()
        deferredBleWrite = null
        writeRetryPending = false
        ++writeGeneration
        ++readGeneration
        pendingReads.clear()
        bleQueue.clear()
        bleAckQueue.clear()
        bleWriting = false
        bleWritingAck = false
        ++bleAckGeneration
        tx = null; rx = null; secretCharacteristic = null
        capabilitiesCharacteristic = null; directLinkCharacteristic = null
        capabilitiesPollScheduled = false
        activeTarget = false
        mtu = 23
        bleImageLimitBytes = 512 * 1024
        bleReassembler.clearPending()
        bleWireReassembler.clearPending()
        reliableReceiver.reset()
        requeueActiveBleTransfer("Bluetooth 연결 정리")
        if (p2p?.ready != true) {
            credential = null
            directConfig = null
            releaseDirectNetwork()
        }
    }
    private val executor = Executors.newCachedThreadPool()
    private val secureRandom = SecureRandom()
    private val bleQueue = ConcurrentLinkedQueue<ByteArray>()
    private val bleAckQueue = ConcurrentLinkedQueue<ByteArray>()
    private val bleReassembler = BleReassembler()
    private val bleWireReassembler = ClipboardWireReassembler()
    private val p2pWireReassembler = ClipboardWireReassembler()
    private val rejectedBleDevices = ConcurrentHashMap.newKeySet<String>()
    private val sharedStore by lazy { AndroidSharedPayloadStore(this) }
    private val backgroundClipboard by lazy { BackgroundClipboardMonitor(this) { drainPersistedShares() } }
    private val loadedPersistentShareIds = HashSet<String>()
    private val applyGate = Any()
    private var lastAppliedHash: ByteArray? = null
    private var lastAppliedAt = 0L

    @Volatile private var gatt: BluetoothGatt? = null
    @Volatile private var tx: BluetoothGattCharacteristic? = null
    @Volatile private var rx: BluetoothGattCharacteristic? = null
    @Volatile private var secretCharacteristic: BluetoothGattCharacteristic? = null
    @Volatile private var capabilitiesCharacteristic: BluetoothGattCharacteristic? = null
    @Volatile private var directLinkCharacteristic: BluetoothGattCharacteristic? = null
    @Volatile private var mtu = 23
    @Volatile private var bleImageLimitBytes = 512 * 1024
    @Volatile private var bleWriting = false
    @Volatile private var bleWritingAck = false
    private val bleSendLock = Any()
    private var bleActiveContent: ClipboardContent? = null
    private var bleActivePersistentShareId: String? = null
    private var bleActiveTransferId: ByteArray? = null
    private var bleAwaitingContentAck = false
    private var bleContentAckReceived = false
    private var bleAckGeneration = 0L
    @Volatile private var credential: Credential? = null
    @Volatile private var directConfig: DirectConfig? = null
    @Volatile private var activeTarget = false
    @Volatile private var p2p: P2PChannel? = null
    private val pendingLock = Any()
    private val pendingQueue = java.util.ArrayDeque<PendingSend>()
    private var pendingBytes = 0L
    private var nextPendingId = 0L
    @Volatile private var p2pNetwork: Network? = null
    @Volatile private var directNetworkCallback: ConnectivityManager.NetworkCallback? = null
    @Volatile private var directConfigReadAttempts = 0
    @Volatile private var capabilitiesPollScheduled = false

    private data class Credential(val deviceHash: ByteArray, val secret: ByteArray)
    private data class DirectConfig(val ssid: String, val passphrase: String, val hostAddress: String?)
    private data class PendingSend(
        val id: Long,
        val content: ClipboardContent,
        val persistedShareId: String?,
        val expiresAt: Long
    )


    private val bluetoothReconnect = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            when (intent?.action) {
                BluetoothAdapter.ACTION_STATE_CHANGED -> when (intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1)) {
                    BluetoothAdapter.STATE_TURNING_OFF, BluetoothAdapter.STATE_OFF -> {
                        bleLifecycle.radioChanged(false)
                        showStatus("Bluetooth OFF · 연결 정리 완료")
                    }
                    BluetoothAdapter.STATE_ON -> bleLifecycle.radioChanged(true)
                }
                BluetoothDevice.ACTION_ACL_CONNECTED -> ensureSelectedPcConnection()
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        @Suppress("DEPRECATION")
        val version = packageManager.getPackageInfo(packageName, 0).versionName
        bleLog("service started version=$version Android=${Build.VERSION.RELEASE} SDK=${Build.VERSION.SDK_INT}")
        createNotificationChannel()
        showStatus("연결 준비 중")
        if (getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).getBoolean(USER_DISCONNECTED, false))
            bleLifecycle.userDisconnect()
        backgroundClipboard.start()
        main.post { drainPersistedShares() }
        val filter = android.content.IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        filter.addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(bluetoothReconnect, filter, RECEIVER_EXPORTED)
        else @Suppress("DEPRECATION") registerReceiver(bluetoothReconnect, filter)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_START) {
            ACTION_STOP -> {
                requestUserDisconnect()
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_DISCONNECT -> requestUserDisconnect()
            ACTION_CONNECT -> {
                getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).edit().putBoolean(USER_DISCONNECTED, false).apply()
                bleLifecycle.resume()
            }
            ACTION_CONNECT_DEVICE -> connectSelectedPc(intent?.getStringExtra(EXTRA_DEVICE_ADDRESS))
            ACTION_SEND -> sendCurrentClipboard()
            ACTION_SEND_SHARED_TEXT -> sendSharedText(
                intent?.getStringExtra(EXTRA_SHARED_TEXT))
            ACTION_SEND_SHARED_IMAGE -> showStatus("이미지는 전달되지 않습니다.")
            ACTION_DRAIN_SHARED -> drainPersistedShares()
            ACTION_RETRY_P2P -> retryP2PNow()
            else -> ensureSelectedPcConnection()
        }
        ensureSelectedPcConnection() // Sharing or draining must also establish a connection on a cold start.
        return START_STICKY
    }

    override fun onBind(intent: Intent?) = null

    private fun requestUserDisconnect() {
        getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).edit().putBoolean(USER_DISCONNECTED, true).apply()
        p2p?.close(); p2p = null
        bleLifecycle.userDisconnect()
        showStatus("연결 해제됨 · 직접 연결할 때까지 자동 재연결 중지")
    }

    private fun ensureSelectedPcConnection() {
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post { ensureSelectedPcConnection() }; return }
        if (destroyed || !bleLifecycle.desired || bleLifecycle.state != BleConnectionState.DISCONNECTED) return
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            showStatus("Bluetooth 권한 필요")
            return
        }

        val manager = getSystemService(BluetoothManager::class.java)
        val adapter = manager.adapter ?: run {
            showStatus("Bluetooth 어댑터 없음")
            return
        }

        if (!adapter.isEnabled) {
            showStatus("Bluetooth를 켜 주세요")
            return
        }

        if (gatt != null) return
        val savedAddress = getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).getString(SAVED_PC_ADDRESS, null)
        if (savedAddress.isNullOrBlank()) {
            showStatus("연결할 PC를 선택해 주세요. 선택한 PC에만 자동 연결합니다.")
            return
        }
        val savedPc = adapter.bondedDevices.firstOrNull { it.address.equals(savedAddress, ignoreCase = true) }
        if (savedPc == null) {
            showStatus("선택한 PC의 페어링이 해제됐어요. Bluetooth 설정에서 PC를 연결해 주세요.")
            return
        }
        if (rejectedBleDevices.contains(savedPc.address)) return
        connectDevice(savedPc)
    }

    private fun connectSelectedPc(address: String?) {
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return
        val adapter = getSystemService(BluetoothManager::class.java).adapter ?: return
        val device = adapter.bondedDevices.firstOrNull { it.address == address }
        if (device == null) { showStatus("먼저 휴대폰 Bluetooth 설정에서 PC와 페어링해 주세요"); return }
        // A selection of the current PC is a connect request, not a forced reconnect.
        if (gatt?.device?.address == device.address) {
            bleLifecycle.resume()
            return
        }
        if (bleLifecycle.state == BleConnectionState.CONNECTING ||
            bleLifecycle.state == BleConnectionState.DISCOVERING_SERVICES ||
            bleLifecycle.state == BleConnectionState.DISCONNECTING) return
        getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).edit()
            .putString(SAVED_PC_ADDRESS, device.address).putBoolean(USER_DISCONNECTED, false).apply()
        rejectedBleDevices.remove(device.address)
        bleLifecycle.resume()
        if (bleLifecycle.resource != null) {
            p2p?.close(); p2p = null
            bleLifecycle.cleanup("selected PC changed", retry = true, graceful = true)
        } else ensureSelectedPcConnection()
    }

    private fun connectDevice(device: BluetoothDevice) {
        if (destroyed || gatt != null) return
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return
        val selectedAddress = getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).getString(SAVED_PC_ADDRESS, null)
        if (!AutoConnectPolicy.canConnect(device.address, selectedAddress,
                device.bondState == BluetoothDevice.BOND_BONDED, gatt != null,
                rejectedBleDevices.contains(device.address))) return
        showStatus("선택한 PC 연결 중 · 클립보드 서비스 확인")
        handshakeComplete = false
        bleLifecycle.connect { session ->
            device.connectGatt(this, false, createGattCallback(session), BluetoothDevice.TRANSPORT_LE,
                BluetoothDevice.PHY_LE_1M_MASK, main).also { gatt = it }
        }
    }

    private fun createGattCallback(session: Long) = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            bleLog("connection callback session=$session status=$status newState=$newState")
            if (bleLifecycle.state == BleConnectionState.DISCONNECTING && newState == BluetoothProfile.STATE_DISCONNECTED) {
                bleLifecycle.finishCleanup(g, session, linkAlreadyDisconnected = true)
                return
            }
            if (!bleLifecycle.accepts(g, session)) return
            if (status != BluetoothGatt.GATT_SUCCESS || newState == BluetoothProfile.STATE_DISCONNECTED) {
                showStatus("PC 연결 끊김 (상태 $status) · 재연결 대기")
                bleLifecycle.cleanup("disconnected status=$status", retry = true,
                    linkAlreadyDisconnected = newState == BluetoothProfile.STATE_DISCONNECTED)
            } else if (newState == BluetoothProfile.STATE_CONNECTED && bleLifecycle.state == BleConnectionState.CONNECTING) {
                bleLifecycle.linkConnected()
                mtu = 23
                // HID and the companion share ATT. The wire protocol works at MTU 23;
                // do not make an optional MTU negotiation a reconnect prerequisite.
                discoverGatt(g)
            }
        }

        override fun onMtuChanged(g: BluetoothGatt, value: Int, status: Int) {
            if (!bleLifecycle.accepts(g, session)) return
            bleLog("MTU status=$status mtu=$value")
            if (status == BluetoothGatt.GATT_SUCCESS) mtu = value.coerceIn(23, 517)
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            if (!bleLifecycle.accepts(g, session) || !bleLifecycle.finishOperation("discovery")) return
            bleLog("services discovered status=$status")
            if (status != BluetoothGatt.GATT_SUCCESS) {
                pauseGattChannel(g, "Clipboard BLE 서비스 확인 실패 status=$status")
                return
            }
            try {
                val service = g.getService(SERVICE_UUID) ?: run {
                    // Optional clipboard service absence must not cycle the HID link.
                    pauseGattChannel(g, "선택한 PC의 LINKO 클립보드 서비스 대기")
                    return
                }
                tx = service.getCharacteristic(TX_UUID)
                rx = service.getCharacteristic(RX_UUID)
                secretCharacteristic = service.getCharacteristic(SECRET_UUID)
                capabilitiesCharacteristic = service.getCharacteristic(CAPABILITIES_UUID)
                directLinkCharacteristic = service.getCharacteristic(DIRECT_LINK_UUID)
                val txLocal = tx
                require(txLocal != null && rx != null && secretCharacteristic != null) { "required characteristic missing" }
                require(txLocal.properties and BluetoothGattCharacteristic.PROPERTY_NOTIFY != 0) { "TX not notifiable" }
                require(rx!!.properties and BluetoothGattCharacteristic.PROPERTY_WRITE != 0) { "RX not writable" }
                val descriptor = txLocal.getDescriptor(CCCD_UUID) ?: error("CCCD missing")
                check(g.setCharacteristicNotification(txLocal, true)) { "notification registration failed" }
                check(bleLifecycle.beginOperation("cccd"))
                check(writeCccd(g, descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)) { "CCCD write rejected" }
                showStatus("BLE Clipboard 준비 중")
            } catch (ex: Exception) {
                bleLog("discovery/notification exception=${ex.javaClass.simpleName}")
                pauseGattChannel(g, "클립보드 특성 또는 알림 설정 실패")
            }
        }

        override fun onDescriptorWrite(g: BluetoothGatt, descriptor: BluetoothGattDescriptor, status: Int) {
            if (descriptor.uuid != CCCD_UUID) return
            if (bleLifecycle.state == BleConnectionState.DISCONNECTING) {
                if (bleLifecycle.resource === g && bleLifecycle.generation == session)
                    bleLog("CCCD disable completed status=$status")
                bleLifecycle.finishCleanup(g, session)
                return
            }
            if (!bleLifecycle.accepts(g, session) || !bleLifecycle.finishOperation("cccd")) return
            bleLog("notification enabled status=$status")
            if (status == BluetoothGatt.GATT_SUCCESS) {
                // Waiting for the user to approve this phone is not a failed BLE handshake.
                bleLifecycle.servicesConfigured()
                secretCharacteristic?.let { readCharacteristic(g, it) }
            }
            else pauseGattChannel(g, "BLE 클립보드 알림 등록 실패 status=$status")
        }

        @Suppress("DEPRECATION")
        override fun onCharacteristicRead(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int
        ) {
            if (!bleLifecycle.accepts(g, session) || !readCompleted(g, characteristic)) return
            bleLog("read ${characteristic.uuid} status=$status")
            if (status == BluetoothGatt.GATT_SUCCESS)
                handleCharacteristicRead(characteristic, characteristic.value ?: byteArrayOf())
            else if (characteristic.uuid == SECRET_UUID) {
                showStatus("PC가 클립보드 연결을 승인하지 않았어요 (상태 $status). PC의 ‘이 기기 사용’과 동기화 설정을 확인하세요.")
                retrySecretRead(g)
            }
            else if (characteristic.uuid == CAPABILITIES_UUID) {
                bleImageLimitBytes = 512 * 1024
                scheduleCapabilitiesRead()
            }
            else if (characteristic.uuid == DIRECT_LINK_UUID) {
                activeTarget = false
                bleAckQueue.clear()
                requeueActiveBleTransfer("활성 대상 변경")
                directConfig = null
                p2p?.close()
                p2p = null
                releaseDirectNetwork()
                showStatus("활성 대상이 변경되어 상태 확인 대기")
                scheduleCapabilitiesRead()
            }

        }

        override fun onCharacteristicRead(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray,
            status: Int
        ) {
            if (!bleLifecycle.accepts(g, session) || !readCompleted(g, characteristic)) return
            bleLog("read ${characteristic.uuid} status=$status")
            if (status == BluetoothGatt.GATT_SUCCESS)
                handleCharacteristicRead(characteristic, value)
            else if (characteristic.uuid == SECRET_UUID) {
                showStatus("PC가 클립보드 연결을 승인하지 않았어요 (상태 $status). PC의 ‘이 기기 사용’과 동기화 설정을 확인하세요.")
                retrySecretRead(g)
            }
            else if (characteristic.uuid == CAPABILITIES_UUID) {
                bleImageLimitBytes = 512 * 1024
                scheduleCapabilitiesRead()
            }
            else if (characteristic.uuid == DIRECT_LINK_UUID) {
                activeTarget = false
                bleAckQueue.clear()
                requeueActiveBleTransfer("활성 대상 변경")
                directConfig = null
                p2p?.close()
                p2p = null
                releaseDirectNetwork()
                showStatus("활성 대상이 변경되어 상태 확인 대기")
                scheduleCapabilitiesRead()
            }

        }

        @Suppress("DEPRECATION")
        override fun onCharacteristicChanged(g: BluetoothGatt, characteristic: BluetoothGattCharacteristic) {
            if (bleLifecycle.accepts(g, session) && handshakeComplete)
                handleCharacteristicChanged(characteristic, characteristic.value ?: byteArrayOf())
        }

        override fun onCharacteristicChanged(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray
        ) {
            if (bleLifecycle.accepts(g, session) && handshakeComplete)
                handleCharacteristicChanged(characteristic, value)
        }

        override fun onCharacteristicWrite(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int
        ) {
            if (!bleLifecycle.accepts(g, session) || characteristic.uuid != RX_UUID ||
                !bleLifecycle.finishOperation("write")) return
            bleWriting = false
            bleWritingAck = false
            ++writeGeneration
            if (status == BluetoothGatt.GATT_SUCCESS) {
                if (pendingReads.isNotEmpty()) drainReads() else writeNextBle()
            } else {
                bleLog("write failed status=$status")
                // A completed ATT error (for example a target/approval change) does not
                // prove the physical link failed. Let status polling recover the channel.
                bleAckQueue.clear()
                activeTarget = false
                requeueActiveBleTransfer("BLE Clipboard 쓰기 거절 status=$status · 연결 유지")
                scheduleCapabilitiesRead()
                if (pendingReads.isNotEmpty()) drainReads()
            }
        }
    }

    private fun retrySecretRead(g: BluetoothGatt) {
        val session = bleLifecycle.generation
        main.postDelayed({
            if (!bleLifecycle.accepts(g, session)) return@postDelayed
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
                secretCharacteristic?.let { readCharacteristic(g, it) }
        }, 5000)
    }

    private fun handleCharacteristicRead(
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray
    ) {
        when (characteristic.uuid) {
            SECRET_UUID -> {
                if (value.size != 64) {
                    showStatus("PC의 LINKO 인증 응답을 확인할 수 없어요 · HID 연결 유지")
                    gatt?.let { retrySecretRead(it) }
                    return
                }
                handshakeComplete = true
                bleLifecycle.ready()
                gatt?.device?.let { pc ->
                    getSharedPreferences(CONNECTION_PREFS, MODE_PRIVATE).edit().putString(SAVED_PC_ADDRESS, pc.address).apply()
                }
                credential = Credential(
                    value.copyOfRange(0, 32),
                    value.copyOfRange(32, 64)
                )
                showStatus("BLE Clipboard 연결됨 · P2P 고속 링크 확인")
                flushPendingContent()
                drainPersistedShares()
                if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                    val capabilities = capabilitiesCharacteristic
                    if (capabilities != null)
                        gatt?.let { g -> readCharacteristic(g, capabilities) }
                    else
                        directLinkCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
                }
            }
            CAPABILITIES_UUID -> {
                bleImageLimitBytes = parseBleImageLimit(value)
                val reportedActive = parseActiveTarget(value)

                if (reportedActive != null) {
                    val changed = activeTarget != reportedActive
                    activeTarget = reportedActive

                    if (!reportedActive) {
                        bleAckQueue.clear()
                        requeueActiveBleTransfer("활성 대상 변경")
                        directConfig = null
                        p2p?.close()
                        p2p = null
                        releaseDirectNetwork()
                        if (changed)
                            showStatus("현재 기기는 대기 대상 · BLE/P2P 전송 비활성")
                    } else {
                        if (changed)
                            showStatus("현재 기기가 활성 대상이 됐습니다.")
                        flushPendingContent()
                        drainPersistedShares()

                        val needP2P =
                            parseP2PRequested(value) ||
                            pendingNeedsP2P()

                        if (needP2P &&
                            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                            directLinkCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
                        }
                    }
                } else if (
                    pendingNeedsP2P() &&
                    checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                    // Older servers do not expose P2P demand. Only probe DirectLink
                    // when a queued local payload actually requires the fast path.
                    directLinkCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
                }

                scheduleCapabilitiesRead()
            }
            DIRECT_LINK_UUID -> {
                // Windows only allows the current active target to read this
                // characteristic. A successful read therefore proves that BLE
                // fallback is authorized even when the PC cannot create P2P.
                activeTarget = true
                directConfig = parseDirectConfig(value)
                directConfigReadAttempts = 0
                flushPendingContent()

                if (directConfig != null) {
                    val direct = p2pNetwork
                    if (direct != null && p2p?.ready != true) {
                        executor.execute {
                            runCatching { connectDirectChannel(direct) }
                                .onFailure { showStatus("P2P TCP 연결 재시도 실패 · BLE fallback 유지") }
                        }
                    } else {
                        showStatus("P2P 고속 링크 준비됨 · 직접 연결 요청")
                        main.postDelayed({
                            if (p2p?.ready != true) requestDirectNetwork()
                        }, 300)
                    }
                } else {
                    retryDirectLinkRead()
                }
            }
        }
    }

    private fun scheduleCapabilitiesRead() {
        if (capabilitiesPollScheduled) return
        val session = bleLifecycle.generation
        val connection = gatt ?: return
        capabilitiesPollScheduled = true

        main.postDelayed({
            if (!bleLifecycle.accepts(connection, session)) return@postDelayed
            capabilitiesPollScheduled = false

            if (gatt != null &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                capabilitiesCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
            }
        }, 5000)
    }

    private fun parseP2PRequested(value: ByteArray): Boolean =
        value.size >= 10 &&
            value[0].toInt() == 2 &&
            value[9].toInt() == 1

    private fun pendingNeedsP2P(): Boolean =
        synchronized(pendingLock) {
            pruneExpiredPendingLocked()
            pendingQueue.peekFirst()?.let { item ->
                item.content.kind != ClipboardKind.TEXT &&
                    item.content.data.size > bleImageLimitBytes
            } ?: false
        }

    private fun parseActiveTarget(value: ByteArray): Boolean? {
        if (value.size < 9 || value[0].toInt() != 2)
            return null

        return value[8].toInt() == 1
    }

    private fun retryDirectLinkRead() {
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post { retryDirectLinkRead() }; return }
        val session = bleLifecycle.generation
        val connection = gatt ?: return
        directConfigReadAttempts++
        val delay = if (directConfigReadAttempts <= 8) 1200L else 5000L

        main.postDelayed({
            if (!bleLifecycle.accepts(connection, session)) return@postDelayed
            if (activeTarget &&
                gatt != null &&
                p2p?.ready != true &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                directLinkCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
            }
        }, delay)
    }

    private fun parseBleImageLimit(value: ByteArray): Int {
        if (value.size < 8 || value[0].toInt() != 2)
            return 512 * 1024

        val limit = ByteBuffer.wrap(value, 4, 4)
            .order(ByteOrder.LITTLE_ENDIAN)
            .int

        return limit.coerceIn(64 * 1024, 4 * 1024 * 1024)
    }

    private fun parseDirectConfig(value: ByteArray): DirectConfig? {
        if (value.size < 3) return null
        val version = value[0].toInt() and 0xFF
        if (version !in 1..2) return null

        val ssidLength = value[1].toInt() and 0xFF
        val passLength = value[2].toInt() and 0xFF
        val header = if (version == 2) 4 else 3
        val hostLength = if (version == 2) value[3].toInt() and 0xFF else 0

        if (ssidLength !in 1..32 ||
            passLength !in 8..63 ||
            hostLength > 45 ||
            value.size != header + ssidLength + passLength + hostLength) return null

        val ssidStart = header
        val passStart = ssidStart + ssidLength
        val passEnd = passStart + passLength
        val ssid = value.copyOfRange(ssidStart, passStart).toString(Charsets.UTF_8)
        val passphrase = value.copyOfRange(passStart, passEnd).toString(Charsets.UTF_8)
        val hostAddress =
            if (hostLength == 0) null
            else value.copyOfRange(passEnd, value.size).toString(Charsets.US_ASCII)
        return DirectConfig(ssid, passphrase, hostAddress)
    }

    private val reliableReceiver = ReliableBleReceiver()

    private fun handleCharacteristicChanged(
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray
    ) {
        if (characteristic.uuid != TX_UUID) return
        runCatching {
            if (value.size >= 10 && value[0] == 0xC7.toByte() && value[1] == 2.toByte()) {
                if (!activeTarget || credential == null) return
                val result = reliableReceiver.receive(value) { processClipboardNotification(it) }
                // Receipt is sent only after validation/reassembly/application succeeded.
                bleAckQueue.add(result.receipt)
                result.applicationAck?.let { enqueueBleAck(it) }
                writeNextBle()
            } else {
                processClipboardNotification(value)?.let { enqueueBleAck(it) }
            }
        }.onFailure {
            showStatus("BLE Clipboard 수신 거부: ${it.message}")
        }
    }

    private fun processClipboardNotification(value: ByteArray): ByteArray? {
        val message = bleReassembler.add(ClipboardProtocol.decodeBle(value)) ?: return null
        val ackId = ClipboardProtocol.decodeAck(message)
        if (ackId != null) {
            completeActiveBleTransferAck(ackId)
            return null
        }
        val chunk = ClipboardProtocol.decode(message)
        val content = bleWireReassembler.add(chunk) ?: return null
        check(activeTarget) { "비활성 기기에 Clipboard를 적용하지 않았습니다." }
        check(applyRemoteClipboardBlocking(content)) { "휴대폰 Clipboard 이미지 적용 실패" }
        return chunk.transferId
    }

    private fun enqueueBleAck(transferId: ByteArray) {
        val peripheral = gatt
        val characteristic = rx

        if (peripheral == null ||
            characteristic == null ||
            credential == null ||
            !activeTarget) {
            showStatus(
                "BLE Clipboard ACK를 보낼 활성 연결이 없습니다.")
            return
        }

        runCatching {
            val maxGatt = maxOf(20, mtu - 3)
            val ack = ClipboardProtocol.encodeAck(transferId)
            val transportId = secureRandom.nextInt().toUInt()

            ClipboardProtocol
                .splitBle(
                    ack,
                    maxGatt,
                    transportId)
                .forEach(bleAckQueue::add)

            writeNextBle()
        }.onFailure {
            bleAckQueue.clear()
            showStatus(
                "BLE Clipboard ACK 준비 실패: ${it.message}")
        }
    }

    private fun connectDirectChannel(network: Network) {
        val cred = credential ?: return
        val config = directConfig ?: return
        val connectivity = getSystemService(ConnectivityManager::class.java)

        var host: String? = null
        for (attempt in 0 until 12) {
            val links = connectivity.getLinkProperties(network)
            host = links?.routes
                ?.asSequence()
                ?.mapNotNull { it.gateway?.hostAddress }
                ?.firstOrNull { it.contains('.') }
                ?: config.hostAddress

            if (!host.isNullOrBlank()) break
            Thread.sleep(250)
        }

        val target = host ?: config.hostAddress
            ?: throw IOException("Windows P2P gateway 주소를 확인하지 못했습니다.")

        val socket = network.socketFactory.createSocket()
        try {
            socket.connect(InetSocketAddress(target, 47911), 3000)
            socket.tcpNoDelay = true
            socket.soTimeout = 5000

            val channel = P2PChannel(
                socket,
                cred,
                ::onP2PFrame,
                ::onP2PClosed
            )
            channel.handshake()

            // Handshake must be bounded, but the authenticated session is long-lived.
            socket.soTimeout = 0
            p2p = channel
            showStatus("Clipboard P2P 고속 채널 연결됨 · $target")
            flushPendingContent()
            executor.execute { channel.readLoop() }
        } catch (error: Exception) {
            runCatching { socket.close() }
            throw error
        }
    }

    private fun retryP2PNow() {
        // Durable Share items may have expired from the in-memory queue while
        // P2P permission was missing. Re-check storage whenever the user asks
        // to enable the fast path.
        drainPersistedShares()

        if (!activeTarget) {
            showStatus("현재 활성 휴대폰 대상이 아니라 P2P 연결을 기다립니다.")
            scheduleCapabilitiesRead()
            return
        }

        if (p2p?.ready == true) {
            showStatus("P2P 고속 채널이 이미 연결되어 있습니다.")
            flushPendingContent()
            drainPersistedShares()
            return
        }

        val currentConfig = directConfig
        if (currentConfig != null) {
            requestDirectNetwork()
            return
        }

        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED) {
            directLinkCharacteristic?.let {
                gatt?.let { g -> readCharacteristic(g, it) }
                return
            }
        }

        showStatus("P2P 설정을 다시 가져오기 위해 Bluetooth 연결을 확인합니다.")
        ensureSelectedPcConnection()
    }

    private fun requestDirectNetwork() {
        if (p2p != null || p2pNetwork != null || directNetworkCallback != null) return
        val config = directConfig ?: return

        val hasPermission =
            if (Build.VERSION.SDK_INT >= 33)
                checkSelfPermission(Manifest.permission.NEARBY_WIFI_DEVICES) == PackageManager.PERMISSION_GRANTED
            else
                checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

        if (!hasPermission) {
            showStatus("P2P 고속 링크 권한 필요 · BLE fallback 유지")
            return
        }

        val specifier = WifiNetworkSpecifier.Builder()
            .setSsid(config.ssid)
            .setWpa2Passphrase(config.passphrase)
            .build()

        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(specifier)
            .build()

        val connectivity = getSystemService(ConnectivityManager::class.java)
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                p2pNetwork = network
                showStatus("P2P Wi-Fi 링크 연결됨 · 보안 채널 승격 중")
                executor.execute {
                    runCatching { connectDirectChannel(network) }
                        .onFailure {
                            showStatus("P2P 데이터 채널 연결 실패 · 주소 재확인: ${it.message}")
                            main.postDelayed({ retryDirectLinkRead() }, 500)
                        }
                }
            }

            override fun onLost(network: Network) {
                if (p2pNetwork == network) p2pNetwork = null
                p2p?.close()
                p2p = null

                if (directNetworkCallback === this) {
                    directNetworkCallback = null
                    runCatching { connectivity.unregisterNetworkCallback(this) }
                }

                showStatus("P2P 링크 종료 · BLE fallback 유지")
                main.postDelayed({
                    if (gatt != null && directConfig != null)
                        requestDirectNetwork()
                }, 1500)
            }

            override fun onUnavailable() {
                directNetworkCallback = null
                directConfig = null
                showStatus("P2P 링크 사용 불가/변경됨 · 설정 재확인")
                retryDirectLinkRead()
            }
        }

        directNetworkCallback = callback
        showStatus("P2P 고속 링크 연결 승인 대기")
        connectivity.requestNetwork(request, callback, 15_000)
    }

    private fun releaseDirectNetwork() {
        val callback = directNetworkCallback ?: return
        directNetworkCallback = null
        p2pNetwork = null
        runCatching {
            getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(callback)
        }
    }

    private fun onP2PFrame(frame: ByteArray): ByteArray? {
        return runCatching {
            val chunk = ClipboardProtocol.decode(frame)
            val content =
                p2pWireReassembler.add(chunk)
                    ?: return@runCatching null

            if (!activeTarget) {
                showStatus(
                    "비활성 기기로 늦게 도착한 P2P Clipboard를 적용하지 않았습니다.")
                return@runCatching null
            }

            if (!applyRemoteClipboardBlocking(content)) {
                showStatus(
                    "P2P Clipboard 적용 실패 · ACK를 보내지 않아 상대 재시도를 유도합니다.")
                return@runCatching null
            }

            ClipboardProtocol.encodeAck(chunk.transferId)
        }.onFailure {
            showStatus(
                "P2P Clipboard 수신 거부: ${it.message}")
        }.getOrNull()
    }

    private fun onP2PClosed() {
        p2p = null
        showStatus("P2P 채널 종료 · BLE fallback 유지")

        val direct = p2pNetwork
        if (activeTarget &&
            directConfig != null &&
            direct != null) {
            executor.execute {
                runCatching { connectDirectChannel(direct) }
                    .onFailure {
                        showStatus("P2P 재연결 실패 · BLE 연결 상태를 다시 확인합니다.")
                    }
            }
        }
    }

    private fun completePersistedShare(id: String?) {
        if (id == null) {
            // A normal clipboard item may have occupied the bounded in-memory queue
            // while a persistent Share item was waiting. Re-check durable storage
            // whenever capacity becomes available.
            main.post { drainPersistedShares() }
        val filter = android.content.IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        filter.addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(bluetoothReconnect, filter, RECEIVER_EXPORTED)
        else @Suppress("DEPRECATION") registerReceiver(bluetoothReconnect, filter)
            return
        }

        val cleared = runCatching {
            sharedStore.clear(id)
        }.onFailure {
            showStatus("Android 공유 저장소 완료 처리 실패: ${it.message}")
        }.isSuccess

        if (!cleared) {
            // Keep the id marked as loaded so the same persistent item cannot
            // immediately be re-sent and duplicated.
            return
        }

        synchronized(pendingLock) {
            loadedPersistentShareIds.remove(id)
        }

        main.post { drainPersistedShares() }
        val filter = android.content.IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        filter.addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(bluetoothReconnect, filter, RECEIVER_EXPORTED)
        else @Suppress("DEPRECATION") registerReceiver(bluetoothReconnect, filter)
    }

    private fun drainPersistedShares() {
        val item = runCatching { sharedStore.peek() }
            .onFailure {
                showStatus("Android 공유 대기열 읽기 실패: ${it.message}")
            }
            .getOrNull()
            ?: return

        val accepted = synchronized(pendingLock) {
            if (item.id in loadedPersistentShareIds) {
                false
            } else {
                loadedPersistentShareIds.add(item.id)
                true
            }
        }
        if (!accepted) return

        executor.execute {
            runCatching {
                when (item.kind) {
                    AndroidSharedPayloadStore.Kind.TEXT -> {
                        val text = item.data.toString(Charsets.UTF_8)
                        ClipboardProtocol.text(text)
                    }
                    AndroidSharedPayloadStore.Kind.IMAGE -> error("이미지는 전달되지 않습니다.")
                }
            }.onSuccess { content ->
                sendContent(
                    content,
                    persistedShareId = item.id)
            }.onFailure { error ->
                val cleared = runCatching {
                    sharedStore.clear(item.id)
                }.onFailure {
                    showStatus(
                        "손상된 Android 공유 항목 삭제 실패: ${it.message}")
                }.isSuccess

                if (cleared) {
                    synchronized(pendingLock) {
                        loadedPersistentShareIds.remove(item.id)
                    }
                    showStatus(
                        "저장된 Android 공유 항목을 전송할 수 없어 제거했습니다: ${error.message}")
                    main.post { drainPersistedShares() }
        val filter = android.content.IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        filter.addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(bluetoothReconnect, filter, RECEIVER_EXPORTED)
        else @Suppress("DEPRECATION") registerReceiver(bluetoothReconnect, filter)
                } else {
                    // Keep it marked as loaded so a corrupt persistent item
                    // cannot spin in a read/fail/re-read loop.
                    showStatus(
                        "저장된 Android 공유 항목 오류를 보존했습니다. 앱 재시작 후 다시 정리합니다.")
                }
            }
        }
    }

    private fun sendSharedText(text: String?) {
        if (text.isNullOrEmpty()) {
            toast("공유할 텍스트가 없습니다.")
            return
        }

        runCatching {
            ClipboardProtocol.text(text)
        }.onSuccess(::sendContent)
            .onFailure { toast("텍스트 전송 준비 실패: ${it.message}") }
    }

    private fun sharedImageContentForWindows(
        bytes: ByteArray,
        mime: String?
    ): ClipboardContent {
        ensureSafeImageDimensions(bytes)

        val encodedKind =
            detectEncodedImageKind(bytes, mime)

        if (encodedKind == ClipboardKind.PNG ||
            encodedKind == ClipboardKind.JPEG) {
            return ClipboardProtocol.image(
                encodedKind,
                bytes)
        }

        val bitmap =
            decodeImageForPngNormalization(bytes)

        return try {
            val out = ByteArrayOutputStream()
            check(
                bitmap.compress(
                    Bitmap.CompressFormat.PNG,
                    100,
                    out)
            ) {
                "공유 이미지를 PNG로 변환하지 못했습니다."
            }

            ClipboardProtocol.image(
                ClipboardKind.PNG,
                out.toByteArray())
        } finally {
            bitmap.recycle()
        }
    }

    private fun detectEncodedImageKind(
        bytes: ByteArray,
        mime: String?
    ): ClipboardKind? {
        if (bytes.size >= 8 &&
            bytes.copyOfRange(0, 8).contentEquals(
                byteArrayOf(137.toByte(), 80, 78, 71, 13, 10, 26, 10)))
            return ClipboardKind.PNG

        if (bytes.size >= 4 &&
            bytes[0] == 0xFF.toByte() &&
            bytes[1] == 0xD8.toByte() &&
            bytes[bytes.size - 2] == 0xFF.toByte() &&
            bytes.last() == 0xD9.toByte())
            return ClipboardKind.JPEG

        if (bytes.size >= 12 &&
            String(bytes, 0, 4, Charsets.US_ASCII) == "RIFF" &&
            String(bytes, 8, 4, Charsets.US_ASCII) == "WEBP")
            return ClipboardKind.WEBP

        return when (mime?.lowercase()) {
            "image/png" -> ClipboardKind.PNG
            "image/jpeg", "image/jpg" -> ClipboardKind.JPEG
            "image/webp" -> ClipboardKind.WEBP
            else -> null
        }?.takeIf { kind ->
            runCatching {
                ClipboardProtocol.validate(kind, bytes)
            }.isSuccess
        }
    }

    private fun sendCurrentClipboard() {
        val clipboard = getSystemService(android.content.ClipboardManager::class.java)
        val clip = clipboard.primaryClip
        if (clip == null || clip.itemCount == 0) {
            toast("현재 클립보드가 비어 있습니다.")
            return
        }

        val item = clip.getItemAt(0)
        val text = item.text?.toString()
        if (!text.isNullOrEmpty()) {
            sendContent(ClipboardProtocol.text(text))
            return
        }

        toast("이미지는 전달되지 않습니다. 텍스트만 지원합니다.")
    }

    private fun sendContent(
        content: ClipboardContent,
        persistedShareId: String? = null
    ) {
        if (content.kind != ClipboardKind.TEXT) {
            showStatus("이미지는 전달되지 않습니다.")
            completePersistedShare(persistedShareId)
            return
        }
        val hasOlderPending = synchronized(pendingLock) {
            pruneExpiredPendingLocked()
            pendingQueue.isNotEmpty()
        }

        if (hasOlderPending) {
            if (setPendingContent(content, persistedShareId))
                flushPendingContent()
            return
        }

        sendContentInternal(content, persistedShareId)
    }

    private fun sendContentInternal(
        content: ClipboardContent,
        persistedShareId: String? = null
    ) {
        val fast = p2p
        if (fast?.ready == true) {
            executor.execute {
                runCatching {
                    fast.sendContent(content)
                }.onSuccess {
                    showStatus("Clipboard 전송 완료 · P2P")
                    completePersistedShare(persistedShareId)
                    flushPendingContent()
                }.onFailure {
                    fast.close()
                    if (p2p === fast) p2p = null
                    sendViaBleOrQueue(content, persistedShareId)
                }
            }
            return
        }

        sendViaBleOrQueue(content, persistedShareId)
    }

    private fun sendViaBleOrQueue(
        content: ClipboardContent,
        persistedShareId: String? = null
    ) {
        val bleAllowed =
            content.kind == ClipboardKind.TEXT ||
            content.data.size <= bleImageLimitBytes

        if (bleAllowed &&
            activeTarget &&
            gatt != null &&
            rx != null &&
            credential != null) {
            enqueueBle(content, persistedShareId)
            return
        }

        if (!setPendingContent(content, persistedShareId))
            return

        showStatus(
            if (!bleAllowed)
                "P2P 연결 후 이미지 전송 대기"
            else
                "PC 연결 후 Clipboard 전송 대기"
        )

        main.post {
            ensureSelectedPcConnection()

            if (!bleAllowed &&
                activeTarget &&
                p2p?.ready != true &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                val direct = directConfig
                if (direct != null)
                    requestDirectNetwork()
                else
                    directLinkCharacteristic?.let { gatt?.let { g -> readCharacteristic(g, it) } }
            } else if (directConfig != null && p2p?.ready != true) {
                requestDirectNetwork()
            }
        }
    }

    private fun flushPendingContent() {
        val pending = synchronized(pendingLock) {
            pruneExpiredPendingLocked()

            val first = pendingQueue.peekFirst() ?: return

            val canP2P = p2p?.ready == true
            val canBle =
                activeTarget &&
                (first.content.kind == ClipboardKind.TEXT ||
                    first.content.data.size <= bleImageLimitBytes) &&
                gatt != null && rx != null && credential != null

            if (!canP2P && !canBle)
                return

            pendingQueue.removeFirst()
            pendingBytes -= first.content.data.size.toLong()
            first
        }

        sendContentInternal(
            pending.content,
            pending.persistedShareId)
    }

    private fun setPendingContent(
        content: ClipboardContent,
        persistedShareId: String? = null
    ): Boolean {
        val pending = synchronized(pendingLock) {
            pruneExpiredPendingLocked()

            val size = content.data.size.toLong()
            if (pendingQueue.size >= MAX_PENDING_ITEMS ||
                pendingBytes + size > MAX_PENDING_BYTES) {
                false
            } else {
                val id = ++nextPendingId
                val item = PendingSend(
                    id = id,
                    content = content,
                    persistedShareId = persistedShareId,
                    expiresAt = System.currentTimeMillis() + PENDING_TTL_MS)
                pendingQueue.addLast(item)
                pendingBytes += size

                main.postDelayed({
                    expirePending(id)
                }, PENDING_TTL_MS)

                true
            }
        }

        if (!pending) {
            if (persistedShareId != null) {
                synchronized(pendingLock) {
                    loadedPersistentShareIds.remove(persistedShareId)
                }
            }
            showStatus("Clipboard 전송 대기열이 가득 찼습니다.")
        }

        return pending
    }

    private fun expirePending(id: Long) {
        val expiredPersistentId = synchronized(pendingLock) {
            val iterator = pendingQueue.iterator()
            var persistedId: String? = null

            while (iterator.hasNext()) {
                val item = iterator.next()
                if (item.id == id) {
                    pendingBytes -= item.content.data.size.toLong()
                    persistedId = item.persistedShareId
                    iterator.remove()
                    break
                }
            }

            persistedId?.let { loadedPersistentShareIds.remove(it) }
            persistedId
        }

        if (expiredPersistentId != null) {
            showStatus(
                "Clipboard 전송 대기 항목이 만료됐지만 Android 공유 저장소에는 보존했습니다. 다음 연결 시 다시 시도합니다.")
        } else {
            showStatus("Clipboard 전송 대기 항목 1개가 만료됐습니다.")
        }
    }

    private fun pruneExpiredPendingLocked() {
        val now = System.currentTimeMillis()
        val iterator = pendingQueue.iterator()

        while (iterator.hasNext()) {
            val item = iterator.next()
            if (item.expiresAt <= now) {
                pendingBytes -= item.content.data.size.toLong()
                item.persistedShareId?.let {
                    loadedPersistentShareIds.remove(it)
                }
                iterator.remove()
            }
        }
    }

    private fun clearPendingContent() {
        synchronized(pendingLock) {
            pendingQueue.clear()
            pendingBytes = 0
            loadedPersistentShareIds.clear()
        }
    }

    private fun pendingSummary(): String =
        synchronized(pendingLock) {
            if (pendingQueue.isEmpty()) ""
            else " · 대기 ${pendingQueue.size}개 / ${pendingBytes / 1024} KiB"
        }

    private fun enqueueBle(
        content: ClipboardContent,
        persistedShareId: String? = null
    ) {
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post { enqueueBle(content, persistedShareId) }; return }
        if (destroyed) return
        val g = gatt
        val rxLocal = rx
        if (g == null || rxLocal == null || credential == null) {
            if (setPendingContent(content, persistedShareId)) {
                showStatus("PC 연결 후 Clipboard 전송 대기")
                main.post { ensureSelectedPcConnection() }
            }
            return
        }

        if (content.kind != ClipboardKind.TEXT && content.data.size > bleImageLimitBytes) {
            toast("BLE 설정 한도를 초과한 이미지는 P2P 고속 연결 후 전송해 주세요.")
            return
        }

        val accepted = synchronized(bleSendLock) {
            if (bleActiveContent != null || bleWriting || bleQueue.isNotEmpty()) {
                false
            } else {
                bleActiveContent = content
                bleActivePersistentShareId = persistedShareId
                true
            }
        }

        if (!accepted) {
            if (setPendingContent(content, persistedShareId))
                showStatus("BLE 전송 중 · 다음 Clipboard를 대기열에 추가했습니다.")
            return
        }

        runCatching {
            val maxGatt = maxOf(20, mtu - 3)
            val frames = ClipboardProtocol
                .encodeSequence(content, 16 * 1024)
                .iterator()

            require(frames.hasNext()) {
                "Clipboard BLE transfer has no frames."
            }

            val firstFrame = frames.next()
            val contentTransferId =
                ClipboardProtocol.decode(firstFrame).transferId

            synchronized(bleSendLock) {
                bleActiveTransferId = contentTransferId.copyOf()
                bleAwaitingContentAck = false
                bleContentAckReceived = false
                bleAckGeneration++
            }

            fun enqueueWireFrame(frame: ByteArray) {
                val transfer = secureRandom.nextInt().toUInt()
                ClipboardProtocol
                    .splitBle(frame, maxGatt, transfer)
                    .forEach(bleQueue::add)
            }

            enqueueWireFrame(firstFrame)
            while (frames.hasNext())
                enqueueWireFrame(frames.next())
        }.onFailure {
            requeueActiveBleTransfer(
                "BLE Clipboard 프레임 준비 실패: ${it.message}")
            return
        }

        writeNextBle()
    }

    private fun completeActiveBleTransferAck(
        transferId: ByteArray
    ) {
        var matched = false
        var completed = false
        var persistedId: String? = null

        synchronized(bleSendLock) {
            val expected = bleActiveTransferId
            if (bleActiveContent != null &&
                expected != null &&
                expected.contentEquals(transferId)) {
                matched = true
                bleContentAckReceived = true

                if (!bleWriting && deferredBleWrite == null && bleQueue.isEmpty()) {
                    completed = true
                    persistedId = bleActivePersistentShareId
                    bleActiveContent = null
                    bleActivePersistentShareId = null
                    bleActiveTransferId = null
                    bleAwaitingContentAck = false
                    bleContentAckReceived = false
                    bleAckGeneration++
                }
            }
        }

        if (!matched) {
            showStatus(
                "일치하지 않거나 늦게 도착한 BLE Clipboard ACK를 무시했습니다.")
            return
        }

        if (!completed) {
            showStatus(
                "Windows 적용 ACK 확인 · 마지막 BLE write 완료 대기")
            return
        }

        showStatus(
            "Clipboard 전송 완료 · BLE · Windows 적용 ACK 확인")
        completePersistedShare(persistedId)
        flushPendingContent()
    }

    private fun requeueActiveBleTransfer(reason: String) {
        deferredBleWrite = null
        writeRetryPending = false
        ++writeGeneration
        val active = synchronized(bleSendLock) {
            val current = bleActiveContent ?: return
            val persistedId = bleActivePersistentShareId
            bleActiveContent = null
            bleActivePersistentShareId = null
            bleActiveTransferId = null
            bleAwaitingContentAck = false
            bleContentAckReceived = false
            bleAckGeneration++
            bleWriting = false
            bleQueue.clear()
            current to persistedId
        }

        if (setPendingContent(active.first, active.second)) {
            showStatus("$reason · Clipboard를 재전송 대기열로 이동했습니다.")
        } else {
            synchronized(pendingLock) {
                active.second?.let { loadedPersistentShareIds.remove(it) }
            }
            showStatus("$reason · 메모리 대기열은 가득 찼지만 영구 공유 항목은 보존합니다.")
        }
    }

    private fun writeNextBle() {
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post { writeNextBle() }; return }
        if (destroyed || !handshakeComplete || !activeTarget || !bleLifecycle.idle ||
            readingGatt != null || bleWriting || writeRetryPending) return

        val g = gatt
        val characteristic = rx

        if (g == null || characteristic == null) {
            if (bleAckQueue.isNotEmpty()) {
                bleAckQueue.clear()
                showStatus(
                    "BLE Clipboard ACK 연결이 끊겨 응답을 취소했습니다.")
            }

            if (bleActiveContent != null)
                requeueActiveBleTransfer(
                    "BLE 연결 준비 안 됨")
            return
        }

        val deferred = deferredBleWrite
        deferredBleWrite = null
        val ackFrame = if (deferred != null) { if (deferred.second) deferred.first else null } else bleAckQueue.poll()
        val contentFrame =
            if (deferred != null && !deferred.second) deferred.first
            else if (ackFrame == null) bleQueue.poll()
            else null
        val next = ackFrame ?: contentFrame

        if (next == null) {
            var completed = false
            var persistedId: String? = null
            var waitGeneration: Long? = null

            synchronized(bleSendLock) {
                if (bleActiveContent != null &&
                    bleActiveTransferId != null) {
                    if (bleContentAckReceived && !bleWriting) {
                        completed = true
                        persistedId = bleActivePersistentShareId
                        bleActiveContent = null
                        bleActivePersistentShareId = null
                        bleActiveTransferId = null
                        bleAwaitingContentAck = false
                        bleContentAckReceived = false
                        bleAckGeneration++
                    } else if (!bleAwaitingContentAck) {
                        bleAwaitingContentAck = true
                        bleWriting = false
                        bleWritingAck = false
                        waitGeneration = ++bleAckGeneration
                    }
                }
            }

            if (completed) {
                showStatus(
                    "Clipboard 전송 완료 · BLE · Windows 적용 ACK 확인")
                completePersistedShare(persistedId)
                flushPendingContent()
                return
            }

            if (waitGeneration != null) {
                showStatus(
                    "Clipboard BLE 프레임 전송 완료 · Windows 적용 ACK 대기")

                val generation = waitGeneration!!
                main.postDelayed({
                    val timedOut = synchronized(bleSendLock) {
                        bleAwaitingContentAck &&
                            !bleContentAckReceived &&
                            bleAckGeneration == generation &&
                            bleActiveContent != null
                    }

                    if (timedOut)
                        requeueActiveBleTransfer(
                            "BLE Clipboard 적용 ACK 시간 초과")
                }, 45_000)
            }
            return
        }

        if (checkSelfPermission(
                Manifest.permission.BLUETOOTH_CONNECT) !=
            PackageManager.PERMISSION_GRANTED) {
            if (ackFrame != null) {
                bleAckQueue.clear()
                showStatus(
                    "Bluetooth 권한이 없어 BLE ACK를 보낼 수 없습니다.")
            } else {
                requeueActiveBleTransfer(
                    "Bluetooth 권한 없음")
            }
            return
        }

        if (!bleLifecycle.beginOperation("write")) return
        bleWriting = true
        bleWritingAck = ackFrame != null
        val writeAttempt = ++writeGeneration

        val result = runCatching {
            if (Build.VERSION.SDK_INT >= 33) {
                g.writeCharacteristic(
                    characteristic,
                    next,
                    BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT)
            } else {
                @Suppress("DEPRECATION")
                run {
                    characteristic.writeType =
                        BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                    characteristic.value = next

                    if (g.writeCharacteristic(characteristic))
                        BluetoothStatusCodes.SUCCESS
                    else
                        BluetoothStatusCodes.ERROR_UNKNOWN
                }
            }

        }.getOrDefault(BluetoothStatusCodes.ERROR_UNKNOWN)
        if (result != BluetoothStatusCodes.SUCCESS) {
            bleLog("write start failed status=$result")
            bleLifecycle.finishOperation("write")
            bleWriting = false
            bleWritingAck = false
            // This frame was never submitted. Keep it ahead of later frames and ACKs.
            deferredBleWrite = next to (ackFrame != null)
            writeRetryPending = true
            main.postDelayed({
                if (!destroyed && gatt === g && writeGeneration == writeAttempt) {
                    writeRetryPending = false
                    if (pendingReads.isNotEmpty()) drainReads() else writeNextBle()
                }
            }, writeStartBackoff.rejected())
        } else writeStartBackoff.accepted()
    }

    private fun sampleSizeForNormalization(
        width: Int,
        height: Int
    ): Int {
        val maxDecodedPixels = 8_000_000L
        var sample = 1

        while (
            (width.toLong() / sample) *
            (height.toLong() / sample) >
            maxDecodedPixels
        ) {
            sample *= 2
        }

        return sample
    }

    private fun decodeImageForPngNormalization(
        bytes: ByteArray
    ): Bitmap {
        val bounds = BitmapFactory.Options().apply {
            inJustDecodeBounds = true
        }
        BitmapFactory.decodeByteArray(
            bytes,
            0,
            bytes.size,
            bounds)

        val width = bounds.outWidth
        val height = bounds.outHeight

        require(
            width > 0 &&
            height > 0 &&
            width.toLong() * height.toLong() <= 40_000_000L
        ) {
            "이미지 해상도가 안전 한도(40MP)를 초과합니다: ${width}x${height}"
        }

        return BitmapFactory.decodeByteArray(
            bytes,
            0,
            bytes.size,
            BitmapFactory.Options().apply {
                inSampleSize =
                    sampleSizeForNormalization(
                        width,
                        height)
            })
            ?: error("지원하지 않는 이미지 형식입니다.")
    }

    private fun ensureSafeImageDimensions(bytes: ByteArray) {
        val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)

        val width = options.outWidth
        val height = options.outHeight
        require(width > 0 && height > 0 &&
            width.toLong() * height.toLong() <= 40_000_000L) {
            "이미지 해상도가 안전 한도(40MP)를 초과합니다: ${width}x${height}"
        }
    }

    private fun decodeClipboardUriToPng(uri: android.net.Uri): ClipboardContent {
        val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        contentResolver.openInputStream(uri).use { input ->
            BitmapFactory.decodeStream(input, null, options)
        }

        val width = options.outWidth
        val height = options.outHeight
        require(width > 0 && height > 0 &&
            width.toLong() * height.toLong() <= 40_000_000L) {
            "이미지 해상도가 안전 한도(40MP)를 초과합니다: ${width}x${height}"
        }

        val sampleSize =
            sampleSizeForNormalization(
                width,
                height)

        val bitmap = contentResolver.openInputStream(uri).use { input ->
            BitmapFactory.decodeStream(
                input,
                null,
                BitmapFactory.Options().apply {
                    inSampleSize = sampleSize
                })
                ?: error("이미지를 읽을 수 없습니다.")
        }

        return try {
            val out = ByteArrayOutputStream()
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
            ClipboardProtocol.image(ClipboardKind.PNG, out.toByteArray())
        } finally {
            bitmap.recycle()
        }
    }

    private fun applyRemoteClipboardBlocking(
        content: ClipboardContent
    ): Boolean {
        if (content.kind != ClipboardKind.TEXT) {
            showStatus("이미지는 전달되지 않습니다.")
            return false
        }
        synchronized(applyGate) {
            val now = System.currentTimeMillis()
            val previous = lastAppliedHash

            if (previous != null &&
                previous.contentEquals(content.hash) &&
                now - lastAppliedAt <= 120_000L) {
                return true
            }
        }

        if (Looper.myLooper() == Looper.getMainLooper()) {
            return applyRemoteClipboardOnMain(content)
        }

        val latch = CountDownLatch(1)
        var applied = false

        main.post {
            try {
                applied = applyRemoteClipboardOnMain(content)
            } finally {
                latch.countDown()
            }
        }

        return try {
            latch.await(8, TimeUnit.SECONDS) && applied
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            false
        }
    }

    private fun applyRemoteClipboardOnMain(
        content: ClipboardContent
    ): Boolean {
        return runCatching {
            val clipboard =
                getSystemService(
                    android.content.ClipboardManager::class.java)

            when (content.kind) {
                ClipboardKind.TEXT -> {
                    val text =
                        content.data.toString(Charsets.UTF_8)
                    lastReceivedText = text
                    clipboard.setPrimaryClip(
                        ClipData.newPlainText(
                            "LINKO",
                            text))
                    toast(
                        "PC 텍스트를 클립보드에 받았습니다.")
                }

                else -> {
                    ensureSafeImageDimensions(content.data)

                    // An immutable .png URI prevents keyboards/chat apps from
                    // caching an older image behind the same /latest URI.
                    val (target, imageUri) = ClipboardImageProvider.newImage(this)

                    if (content.kind == ClipboardKind.PNG) {
                        // Windows clipboard images are already normalized to PNG.
                        // Preserve encoded bytes and avoid a full bitmap allocation.
                        target.writeBytes(content.data)
                    } else {
                        val bitmap =
                            decodeImageForPngNormalization(
                                content.data)
                        try {
                            FileOutputStream(target).use { out ->
                                check(
                                    bitmap.compress(
                                        Bitmap.CompressFormat.PNG,
                                        100,
                                        out)
                                ) {
                                    "수신 이미지를 PNG로 저장하지 못했습니다."
                                }
                            }
                        } finally {
                            bitmap.recycle()
                        }
                    }

                    val clip =
                        ClipData.newUri(
                            contentResolver,
                            "LINKO image",
                            imageUri)
                    clipboard.setPrimaryClip(clip)
                    ClipboardImageProvider.remember(this, imageUri)
                    toast(
                        "PC 이미지 수신 완료 · 붙여넣기 또는 LINKO의 ‘받은 이미지 공유’를 사용하세요.")
                }
            }

            synchronized(applyGate) {
                lastAppliedHash = content.hash.copyOf()
                lastAppliedAt = System.currentTimeMillis()
            }

            true
        }.onFailure {
            toast(
                "클립보드 적용 실패: ${it.message}")
        }.getOrDefault(false)
    }

    private var foregroundStarted = false
    private var notificationSummary: String? = null

    private fun showStatus(text: String) {
        // Some transfer callbacks run on a worker. Serialize publication so an
        // older status cannot race a new one or start the foreground twice.
        if (Looper.myLooper() != Looper.getMainLooper()) { main.post { showStatus(text) }; return }
        if (destroyed) return
        latestStatus = text + pendingSummary()
        val summary = FriendlyStatus.summary(text)
        if (foregroundStarted && notificationSummary == summary) return
        val manager = getSystemService(NotificationManager::class.java)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_linko_notification)
            .setContentTitle("LINKO")
            .setContentText(summary)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .build()
        if (!foregroundStarted) {
            startForeground(NOTIFICATION_ID, notification)
            foregroundStarted = true
        } else manager.notify(NOTIFICATION_ID, notification)
        notificationSummary = summary
    }

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "LINKO 연결", NotificationManager.IMPORTANCE_LOW)
        )
    }

    private fun toast(text: String) =
        main.post { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }

    override fun onDestroy() {
        destroyed = true
        bleLifecycle.shutdown()
        runCatching { backgroundClipboard.close() }
        runCatching { unregisterReceiver(bluetoothReconnect) }
        latestStatus = "연결 서비스가 중지됐어요."
        main.removeCallbacksAndMessages(null)
        synchronized(bleSendLock) {
            bleActiveContent = null
            bleActivePersistentShareId = null
            bleActiveTransferId = null
            bleAwaitingContentAck = false
            bleContentAckReceived = false
            bleAckGeneration++
            bleWriting = false
            bleQueue.clear()
        }
        clearPendingContent()
        p2p?.close()
        p2p = null
        releaseDirectNetwork()
        executor.shutdownNow()
        super.onDestroy()
    }

    private class P2PChannel(
        private val socket: Socket,
        private val credential: Credential,
        private val onFrame: (ByteArray) -> ByteArray?,
        private val onClosed: () -> Unit
    ) {
        private val input = DataInputStream(BufferedInputStream(socket.getInputStream()))
        private val output = BufferedOutputStream(socket.getOutputStream())
        private val sendLock = Any()
        private val ackWaiters = ConcurrentHashMap<String, CompletableFuture<Unit>>()
        @Volatile private var sessionKey: ByteArray? = null
        private var sendSequence = 0L
        private var receiveSequence = 0L
        @Volatile private var closed = false
        val ready: Boolean get() = !closed && socket.isConnected

        private val aad = "ConPhone-Clipboard-P2P-v2".toByteArray(Charsets.US_ASCII) + credential.deviceHash

        fun handshake() {
            val hello = "CPHELLO2".toByteArray(Charsets.US_ASCII)
            val clientNonce = ByteArray(16).also(SecureRandom()::nextBytes)
            val signed = hello + credential.deviceHash + clientNonce
            val mac = hmac(credential.secret, signed)
            output.write(signed + mac)
            output.flush()

            val response = ByteArray(56)
            input.readFully(response)
            require(String(response, 0, 8, Charsets.US_ASCII) == "CPREADY2")
            val serverNonce = response.copyOfRange(8, 24)
            val serverMac = response.copyOfRange(24, 56)
            val expected = hmac(
                credential.secret,
                "CPREADY2".toByteArray(Charsets.US_ASCII) + clientNonce + serverNonce
            )
            require(MessageDigest.isEqual(serverMac, expected)) { "P2P authentication failed." }

            sessionKey = P2PSecurity.deriveSessionKey(
                credential.secret,
                credential.deviceHash,
                clientNonce,
                serverNonce
            )
        }

        fun sendContent(content: ClipboardContent) {
            val frames = ClipboardProtocol.encodeSequence(content).iterator()
            require(frames.hasNext()) { "Clipboard transfer has no frames." }

            val first = frames.next()
            val transferId = ClipboardProtocol.decode(first).transferId
            val key = transferKey(transferId)
            val ack = CompletableFuture<Unit>()

            check(ackWaiters.putIfAbsent(key, ack) == null) {
                "Clipboard ACK transfer id collision."
            }

            try {
                sendFrame(first)
                while (frames.hasNext())
                    sendFrame(frames.next())

                ack.get(45, TimeUnit.SECONDS)
            } finally {
                ackWaiters.remove(key, ack)
            }
        }

        fun sendFrame(plaintext: ByteArray) {
            synchronized(sendLock) {
                check(!closed)
                sendSequence = Math.addExact(sendSequence, 1L)
                val sequenceBytes = ByteBuffer.allocate(8)
                    .order(ByteOrder.LITTLE_ENDIAN)
                    .putLong(sendSequence)
                    .array()

                val key = sessionKey ?: error("P2P session key is not ready.")
                val nonce = ByteArray(12).also(SecureRandom()::nextBytes)
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(
                    Cipher.ENCRYPT_MODE,
                    SecretKeySpec(key, "AES"),
                    GCMParameterSpec(128, nonce)
                )
                cipher.updateAAD(aad + sequenceBytes)
                val encrypted = cipher.doFinal(plaintext)
                val length = 12 + 8 + encrypted.size

                val prefix = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(length).array()
                output.write(prefix)
                output.write(nonce)
                output.write(sequenceBytes)
                output.write(encrypted)
                output.flush()
            }
        }

        fun readLoop() {
            try {
                while (!closed) {
                    val prefix = ByteArray(4)
                    input.readFully(prefix)
                    val length = ByteBuffer.wrap(prefix).order(ByteOrder.LITTLE_ENDIAN).int
                    require(length in 36..(1024 * 1024 + 70 + 36))
                    val body = ByteArray(length)
                    input.readFully(body)
                    val nonce = body.copyOfRange(0, 12)
                    val sequenceBytes = body.copyOfRange(12, 20)
                    val sequence = ByteBuffer.wrap(sequenceBytes)
                        .order(ByteOrder.LITTLE_ENDIAN)
                        .long

                    require(sequence == receiveSequence + 1) {
                        "P2P frame sequence rejected."
                    }

                    val encrypted = body.copyOfRange(20, body.size)

                    val key = sessionKey ?: error("P2P session key is not ready.")
                    val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                    cipher.init(
                        Cipher.DECRYPT_MODE,
                        SecretKeySpec(key, "AES"),
                        GCMParameterSpec(128, nonce)
                    )
                    cipher.updateAAD(aad + sequenceBytes)
                    val plaintext = cipher.doFinal(encrypted)
                    receiveSequence = sequence

                    val ackId = ClipboardProtocol.decodeAck(plaintext)
                    if (ackId != null) {
                        ackWaiters.remove(transferKey(ackId))?.complete(Unit)
                        continue
                    }

                    val response = onFrame(plaintext)
                    if (response != null)
                        sendFrame(response)
                }
            } catch (_: Exception) {
            } finally {
                close()
                onClosed()
            }
        }

        fun close() {
            if (closed) return
            closed = true
            sessionKey?.fill(0)
            sessionKey = null

            val error = IOException("P2P clipboard channel closed.")
            ackWaiters.values.forEach { it.completeExceptionally(error) }
            ackWaiters.clear()

            runCatching { socket.close() }
        }

        private fun transferKey(transferId: ByteArray): String =
            Base64.encodeToString(
                transferId,
                Base64.NO_WRAP or Base64.NO_PADDING)

        private fun hmac(key: ByteArray, data: ByteArray): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(key, "HmacSHA256"))
            return mac.doFinal(data)
        }
    }
}

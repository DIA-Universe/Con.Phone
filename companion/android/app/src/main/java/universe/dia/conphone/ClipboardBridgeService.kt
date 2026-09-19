package universe.dia.conphone

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.*
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
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
        const val ACTION_START = "universe.dia.conphone.START"
        const val ACTION_SEND = "universe.dia.conphone.SEND"
        const val ACTION_SEND_SHARED_TEXT = "universe.dia.conphone.SEND_SHARED_TEXT"
        const val ACTION_SEND_SHARED_IMAGE = "universe.dia.conphone.SEND_SHARED_IMAGE"
        const val ACTION_DRAIN_SHARED = "universe.dia.conphone.DRAIN_SHARED"
        const val ACTION_STOP = "universe.dia.conphone.STOP"

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

    private val main = Handler(Looper.getMainLooper())
    private val executor = Executors.newCachedThreadPool()
    private val secureRandom = SecureRandom()
    private val bleQueue = ConcurrentLinkedQueue<ByteArray>()
    private val bleAckQueue = ConcurrentLinkedQueue<ByteArray>()
    private val bleReassembler = BleReassembler()
    private val bleWireReassembler = ClipboardWireReassembler()
    private val p2pWireReassembler = ClipboardWireReassembler()
    private val sharedStore by lazy { AndroidSharedPayloadStore(this) }
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
    @Volatile private var scanning = false
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


    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        showStatus("연결 준비 중")
        main.post { drainPersistedShares() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_START) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_SEND -> sendCurrentClipboard()
            ACTION_SEND_SHARED_TEXT -> sendSharedText(
                intent?.getStringExtra(EXTRA_SHARED_TEXT))
            ACTION_SEND_SHARED_IMAGE -> sendSharedImage(
                intent?.getStringExtra(EXTRA_SHARED_IMAGE_PATH),
                intent?.getStringExtra(EXTRA_SHARED_IMAGE_MIME))
            ACTION_DRAIN_SHARED -> drainPersistedShares()
            else -> beginScan()
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?) = null

    private fun beginScan() {
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED ||
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
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

        if (scanning || gatt != null) return
        val scanner = adapter.bluetoothLeScanner ?: run {
            showStatus("BLE 스캐너 사용 불가")
            return
        }

        val filter = ScanFilter.Builder()
            .setServiceUuid(ParcelUuid(SERVICE_UUID))
            .build()
        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .build()

        scanning = true
        showStatus("Con.Phone PC 검색 중")
        scanner.startScan(listOf(filter), settings, scanCallback)
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return

            val adapter = getSystemService(BluetoothManager::class.java).adapter ?: return
            runCatching { adapter.bluetoothLeScanner?.stopScan(this) }
            scanning = false

            showStatus("PC BLE 연결 중")
            gatt = result.device.connectGatt(
                this@ClipboardBridgeService,
                true,
                gattCallback,
                BluetoothDevice.TRANSPORT_LE
            )
        }

        override fun onScanFailed(errorCode: Int) {
            scanning = false
            showStatus("BLE 검색 실패: $errorCode")
            main.postDelayed({ beginScan() }, 3000)
        }
    }

    private val gattCallback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                showStatus("BLE 연결됨 · 서비스 확인")
                if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                    g.requestMtu(517)
                    g.discoverServices()
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                bleAckQueue.clear()
                bleWritingAck = false
                requeueActiveBleTransfer("Bluetooth 연결 끊김")
                if (gatt === g) gatt = null
                tx = null
                rx = null
                secretCharacteristic = null
                capabilitiesCharacteristic = null
                capabilitiesPollScheduled = false
                activeTarget = false
                directLinkCharacteristic = null
                bleImageLimitBytes = 512 * 1024
                val dataPlaneAlive = p2p?.ready == true
                if (!dataPlaneAlive) {
                    credential = null
                    directConfig = null
                    releaseDirectNetwork()
                }

                showStatus(if (dataPlaneAlive)
                    "Bluetooth 연결 끊김 · 기존 P2P Clipboard 채널 유지"
                else
                    "PC 연결 끊김 · 재연결 대기")
                runCatching { g.close() }
                main.postDelayed({ beginScan() }, 2500)
            }
        }

        override fun onMtuChanged(g: BluetoothGatt, value: Int, status: Int) {
            if (status == BluetoothGatt.GATT_SUCCESS) mtu = value
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                showStatus("Clipboard BLE 서비스 확인 실패")
                return
            }

            val service = g.getService(SERVICE_UUID) ?: run {
                showStatus("PC에 Clipboard 서비스 없음")
                return
            }

            tx = service.getCharacteristic(TX_UUID)
            rx = service.getCharacteristic(RX_UUID)
            secretCharacteristic = service.getCharacteristic(SECRET_UUID)
            capabilitiesCharacteristic = service.getCharacteristic(CAPABILITIES_UUID)
            directLinkCharacteristic = service.getCharacteristic(DIRECT_LINK_UUID)

            val txLocal = tx ?: return
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return

            g.setCharacteristicNotification(txLocal, true)
            val descriptor = txLocal.getDescriptor(CCCD_UUID)
            if (descriptor != null) {
                if (Build.VERSION.SDK_INT >= 33) {
                    g.writeDescriptor(descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
                } else {
                    @Suppress("DEPRECATION")
                    run {
                        descriptor.value = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
                        g.writeDescriptor(descriptor)
                    }
                }
            } else {
                secretCharacteristic?.let { g.readCharacteristic(it) }
            }
            showStatus("BLE Clipboard 준비 중")
        }

        override fun onDescriptorWrite(
            g: BluetoothGatt,
            descriptor: BluetoothGattDescriptor,
            status: Int
        ) {
            if (descriptor.uuid == CCCD_UUID) {
                if (status == BluetoothGatt.GATT_SUCCESS) {
                    secretCharacteristic?.let { g.readCharacteristic(it) }
                } else {
                    showStatus("BLE Clipboard 알림 구독 실패: $status")
                }
            }
        }

        @Suppress("DEPRECATION")
        override fun onCharacteristicRead(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int
        ) {
            if (status == BluetoothGatt.GATT_SUCCESS)
                handleCharacteristicRead(characteristic, characteristic.value ?: byteArrayOf())
            else if (characteristic.uuid == SECRET_UUID)
                retrySecretRead(g)
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
            if (status == BluetoothGatt.GATT_SUCCESS)
                handleCharacteristicRead(characteristic, value)
            else if (characteristic.uuid == SECRET_UUID)
                retrySecretRead(g)
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
            handleCharacteristicChanged(characteristic, characteristic.value ?: byteArrayOf())
        }

        override fun onCharacteristicChanged(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray
        ) {
            handleCharacteristicChanged(characteristic, value)
        }

        override fun onCharacteristicWrite(
            g: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int
        ) {
            if (characteristic.uuid == RX_UUID) {
                val wasAck = bleWritingAck
                bleWriting = false
                bleWritingAck = false

                if (status == BluetoothGatt.GATT_SUCCESS) {
                    writeNextBle()
                } else if (wasAck) {
                    bleAckQueue.clear()
                    showStatus(
                        "BLE Clipboard ACK 전송 실패: $status · PC가 재전송 여부를 판단합니다.")
                    main.postDelayed({ beginScan() }, 1000)
                    writeNextBle()
                } else {
                    requeueActiveBleTransfer(
                        "BLE Clipboard 쓰기 실패: $status")
                    main.postDelayed({ beginScan() }, 1000)
                }
            }
        }
    }

    private fun retrySecretRead(g: BluetoothGatt) {
        main.postDelayed({
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
                secretCharacteristic?.let { g.readCharacteristic(it) }
        }, 2000)
    }

    private fun handleCharacteristicRead(
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray
    ) {
        when (characteristic.uuid) {
            SECRET_UUID -> {
                if (value.size != 64) return
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
                        gatt?.readCharacteristic(capabilities)
                    else
                        directLinkCharacteristic?.let { gatt?.readCharacteristic(it) }
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

                        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
                            directLinkCharacteristic?.let { gatt?.readCharacteristic(it) }
                    }
                } else if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                    // Compatibility with older v2 servers that do not expose byte [8].
                    directLinkCharacteristic?.let { gatt?.readCharacteristic(it) }
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
        capabilitiesPollScheduled = true

        main.postDelayed({
            capabilitiesPollScheduled = false

            if (gatt != null &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                capabilitiesCharacteristic?.let { gatt?.readCharacteristic(it) }
            }
        }, 5000)
    }

    private fun parseActiveTarget(value: ByteArray): Boolean? {
        if (value.size < 9 || value[0].toInt() != 2)
            return null

        return value[8].toInt() == 1
    }

    private fun retryDirectLinkRead() {
        directConfigReadAttempts++
        val delay = if (directConfigReadAttempts <= 8) 1200L else 5000L

        main.postDelayed({
            if (activeTarget &&
                gatt != null &&
                p2p?.ready != true &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
                directLinkCharacteristic?.let { gatt?.readCharacteristic(it) }
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

    private fun handleCharacteristicChanged(
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray
    ) {
        if (characteristic.uuid != TX_UUID) return

        runCatching {
            val message =
                bleReassembler.add(
                    ClipboardProtocol.decodeBle(value))
                    ?: return

            val ackId = ClipboardProtocol.decodeAck(message)
            if (ackId != null) {
                completeActiveBleTransferAck(ackId)
                return
            }

            val chunk = ClipboardProtocol.decode(message)
            val content =
                bleWireReassembler.add(chunk)
                    ?: return

            if (!activeTarget) {
                showStatus(
                    "비활성 기기로 늦게 도착한 BLE Clipboard를 적용하지 않았습니다.")
                return
            }

            if (applyRemoteClipboardBlocking(content))
                enqueueBleAck(chunk.transferId)
            else
                showStatus(
                    "BLE Clipboard 적용 실패 · ACK를 보내지 않아 PC 재시도를 유도합니다.")
        }.onFailure {
            showStatus(
                "BLE Clipboard 수신 거부: ${it.message}")
        }
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
                    AndroidSharedPayloadStore.Kind.IMAGE -> {
                        sharedImageContentForWindows(
                            item.data,
                            item.mime)
                    }
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

    private fun sendSharedImage(
        path: String?,
        mime: String?
    ) {
        if (path.isNullOrWhiteSpace()) {
            toast("공유할 이미지가 없습니다.")
            return
        }

        executor.execute {
            val file = runCatching {
                val sharedRoot = File(cacheDir, "shared").canonicalFile
                val candidate = File(path).canonicalFile
                require(candidate.path.startsWith(sharedRoot.path + File.separator)) {
                    "허용되지 않은 공유 이미지 경로입니다."
                }
                require(candidate.isFile &&
                    candidate.length() in 1..ClipboardProtocol.MAX_IMAGE.toLong()) {
                    "공유 이미지 임시 파일 크기가 올바르지 않습니다."
                }
                candidate
            }.getOrElse {
                toast("이미지 공유 준비 실패: ${it.message}")
                return@execute
            }

            runCatching {
                val bytes = file.readBytes()
                sharedImageContentForWindows(
                    bytes,
                    mime)
            }.onSuccess(::sendContent)
                .onFailure { toast("이미지 전송 준비 실패: ${it.message}") }

            runCatching { file.delete() }
        }
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

        val uri = item.uri
        if (uri != null) {
            executor.execute {
                runCatching {
                    decodeClipboardUriToPng(uri)
                }.onSuccess(::sendContent)
                    .onFailure { toast("이미지 전송 준비 실패: ${it.message}") }
            }
            return
        }

        toast("텍스트 또는 이미지만 지원합니다.")
    }

    private fun sendContent(
        content: ClipboardContent,
        persistedShareId: String? = null
    ) {
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
            beginScan()
            if (directConfig != null && p2p?.ready != true)
                requestDirectNetwork()
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
        val g = gatt
        val rxLocal = rx
        if (g == null || rxLocal == null || credential == null) {
            if (setPendingContent(content, persistedShareId)) {
                showStatus("PC 연결 후 Clipboard 전송 대기")
                main.post { beginScan() }
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

                if (!bleWriting && bleQueue.isEmpty()) {
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
        if (bleWriting) return

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

        val ackFrame = bleAckQueue.poll()
        val contentFrame =
            if (ackFrame == null) bleQueue.poll()
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

        bleWriting = true
        bleWritingAck = ackFrame != null

        val result =
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

        if (result != BluetoothStatusCodes.SUCCESS) {
            val wasAck = bleWritingAck
            bleWriting = false
            bleWritingAck = false

            if (wasAck) {
                bleAckQueue.clear()
                showStatus(
                    "BLE Clipboard ACK 쓰기 시작 실패: $result")
                main.postDelayed(
                    { beginScan() },
                    1000)
                writeNextBle()
            } else {
                requeueActiveBleTransfer(
                    "BLE Clipboard 쓰기 시작 실패: $result")
                main.postDelayed(
                    { beginScan() },
                    1000)
            }
        }
    }

    private fun sampleSizeForNormalization(
        width: Int,
        height: Int
    ): Int {
        const val maxDecodedPixels = 8_000_000L
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
                    clipboard.setPrimaryClip(
                        ClipData.newPlainText(
                            "Con.Phone",
                            text))
                    toast(
                        "PC 텍스트를 클립보드에 받았습니다.")
                }

                else -> {
                    ensureSafeImageDimensions(content.data)

                    val target =
                        File(
                            cacheDir,
                            ClipboardImageProvider.FILE_NAME)

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
                            "Con.Phone image",
                            ClipboardImageProvider.URI)
                    clipboard.setPrimaryClip(clip)
                    toast(
                        "PC 이미지를 클립보드에 받았습니다.")
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

    private fun showStatus(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("Con.Phone Companion")
            .setContentText(text + pendingSummary())
            .setOngoing(true)
            .build()
        startForeground(NOTIFICATION_ID, notification)
        manager.notify(NOTIFICATION_ID, notification)
    }

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "Con.Phone 연결", NotificationManager.IMPORTANCE_LOW)
        )
    }

    private fun toast(text: String) =
        main.post { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }

    override fun onDestroy() {
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
        scanning = false
        p2p?.close()
        p2p = null
        releaseDirectNetwork()
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
            runCatching { gatt?.close() }
        gatt = null
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

            sessionKey = hmac(
                credential.secret,
                "ConPhone-P2P-Session-v2".toByteArray(Charsets.US_ASCII) +
                    credential.deviceHash +
                    clientNonce +
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

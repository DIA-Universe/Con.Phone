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
import java.io.*
import java.net.*
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class ClipboardBridgeService : Service() {
    companion object {
        const val ACTION_START = "universe.dia.conphone.START"
        const val ACTION_SEND = "universe.dia.conphone.SEND"
        const val ACTION_STOP = "universe.dia.conphone.STOP"

        private const val CHANNEL = "conphone_bridge"
        private const val NOTIFICATION_ID = 7310
        private const val DISCOVERY_PORT = 47910

        private val SERVICE_UUID = UUID.fromString("67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val TX_UUID = UUID.fromString("67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val RX_UUID = UUID.fromString("67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val SECRET_UUID = UUID.fromString("67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val DIRECT_LINK_UUID = UUID.fromString("67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001")
        private val CCCD_UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
    }

    private val main = Handler(Looper.getMainLooper())
    private val executor = Executors.newCachedThreadPool()
    private val secureRandom = SecureRandom()
    private val bleQueue = ConcurrentLinkedQueue<ByteArray>()
    private val bleReassembler = BleReassembler()
    private val wireReassembler = ClipboardWireReassembler()

    @Volatile private var gatt: BluetoothGatt? = null
    @Volatile private var tx: BluetoothGattCharacteristic? = null
    @Volatile private var rx: BluetoothGattCharacteristic? = null
    @Volatile private var secretCharacteristic: BluetoothGattCharacteristic? = null
    @Volatile private var directLinkCharacteristic: BluetoothGattCharacteristic? = null
    @Volatile private var mtu = 23
    @Volatile private var bleWriting = false
    @Volatile private var credential: Credential? = null
    @Volatile private var directConfig: DirectConfig? = null
    @Volatile private var lan: LanChannel? = null
    @Volatile private var scanning = false
    @Volatile private var lanDiscoveryRunning = false
    @Volatile private var p2pNetwork: Network? = null
    @Volatile private var directNetworkCallback: ConnectivityManager.NetworkCallback? = null

    private data class Credential(val deviceHash: ByteArray, val secret: ByteArray)
    private data class DirectConfig(val ssid: String, val passphrase: String)

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        showStatus("연결 준비 중")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_START) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_SEND -> sendCurrentClipboard()
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
                if (gatt === g) gatt = null
                tx = null
                rx = null
                secretCharacteristic = null
                directLinkCharacteristic = null
                credential = null
                directConfig = null
                releaseDirectNetwork()
                lan?.close()
                lan = null
                showStatus("PC 연결 끊김 · 재연결 대기")
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
            else if (characteristic.uuid == DIRECT_LINK_UUID)
                showStatus("P2P 설정 읽기 실패 · LAN/BLE fallback 유지")
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
            else if (characteristic.uuid == DIRECT_LINK_UUID)
                showStatus("P2P 설정 읽기 실패 · LAN/BLE fallback 유지")
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
                bleWriting = false
                if (status == BluetoothGatt.GATT_SUCCESS) writeNextBle()
                else showStatus("BLE Clipboard 쓰기 실패: $status")
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
                credential = Credential(value.copyOfRange(0, 32), value.copyOfRange(32, 64))
                showStatus("BLE Clipboard 연결됨 · LAN/P2P 승격 확인")
                startLanDiscovery()
                if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
                    directLinkCharacteristic?.let { gatt?.readCharacteristic(it) }
            }
            DIRECT_LINK_UUID -> {
                directConfig = parseDirectConfig(value)
                if (directConfig != null) {
                    showStatus("P2P 고속 링크 준비됨 · 현재 LAN 우선 확인")
                    main.postDelayed({
                        if (lan == null) requestDirectNetwork()
                    }, 3500)
                }
            }
        }
    }

    private fun parseDirectConfig(value: ByteArray): DirectConfig? {
        if (value.isEmpty() || value[0].toInt() != 1 || value.size < 3) return null
        val ssidLength = value[1].toInt() and 0xFF
        val passLength = value[2].toInt() and 0xFF
        if (ssidLength !in 1..32 || passLength !in 8..63 ||
            value.size != 3 + ssidLength + passLength) return null

        val ssid = value.copyOfRange(3, 3 + ssidLength).toString(Charsets.UTF_8)
        val passphrase = value.copyOfRange(3 + ssidLength, value.size).toString(Charsets.UTF_8)
        return DirectConfig(ssid, passphrase)
    }

    private fun handleCharacteristicChanged(
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray
    ) {
        if (characteristic.uuid != TX_UUID) return
        runCatching {
            val message = bleReassembler.add(ClipboardProtocol.decodeBle(value)) ?: return
            val content = wireReassembler.add(ClipboardProtocol.decode(message)) ?: return
            applyRemoteClipboard(content)
        }.onFailure {
            showStatus("BLE Clipboard 수신 거부: ${it.message}")
        }
    }

    private fun startLanDiscovery() {
        if (credential == null || lan != null || lanDiscoveryRunning) return
        lanDiscoveryRunning = true
        executor.execute {
            try {
                var attempts = 0
                while (credential != null && lan == null && !Thread.currentThread().isInterrupted) {
                    attempts++
                    runCatching { discoverAndConnectLan(null) }
                    if (lan != null) break

                    if (attempts >= 2 && directConfig != null) {
                        main.post { requestDirectNetwork() }
                        break
                    }
                    Thread.sleep(1600)
                }
            } finally {
                lanDiscoveryRunning = false
            }
        }
    }

    private fun discoverAndConnectLan(network: Network?) {
        val cred = credential ?: return
        DatagramSocket().use { udp ->
            network?.bindSocket(udp)
            udp.broadcast = true
            udp.soTimeout = 1600
            val query = "CONPHONE_DISCOVER_V1".toByteArray(Charsets.US_ASCII)
            udp.send(DatagramPacket(query, query.size, InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT))

            val buffer = ByteArray(128)
            val response = DatagramPacket(buffer, buffer.size)
            udp.receive(response)
            val text = String(response.data, 0, response.length, Charsets.US_ASCII)
            if (!text.startsWith("CONPHONE_HERE_V1:")) return
            val port = text.substringAfterLast(':').toIntOrNull() ?: return

            val socket = network?.socketFactory?.createSocket() ?: Socket()
            socket.connect(InetSocketAddress(response.address, port), 2500)
            socket.tcpNoDelay = true
            socket.soTimeout = 0

            val channel = LanChannel(
                socket,
                cred,
                ::onLanFrame,
                ::onLanClosed
            )
            channel.handshake()
            lan = channel
            showStatus(if (network == null)
                "Clipboard LAN 고속 채널 연결됨"
            else
                "Clipboard P2P 고속 채널 연결됨")
            executor.execute { channel.readLoop() }
        }
    }

    private fun requestDirectNetwork() {
        if (lan != null || p2pNetwork != null || directNetworkCallback != null) return
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
                    runCatching { discoverAndConnectLan(network) }
                        .onFailure { showStatus("P2P 데이터 채널 연결 실패 · BLE fallback 유지") }
                }
            }

            override fun onLost(network: Network) {
                if (p2pNetwork == network) p2pNetwork = null
                if (lan == null) showStatus("P2P 링크 종료 · BLE fallback 유지")
            }

            override fun onUnavailable() {
                directNetworkCallback = null
                showStatus("P2P 링크 사용 불가/거부 · BLE fallback 유지")
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

    private fun onLanFrame(frame: ByteArray) {
        runCatching {
            val content = wireReassembler.add(ClipboardProtocol.decode(frame)) ?: return
            applyRemoteClipboard(content)
        }.onFailure {
            showStatus("LAN Clipboard 수신 거부: ${it.message}")
        }
    }

    private fun onLanClosed() {
        lan = null
        showStatus("LAN/P2P 종료 · BLE fallback 유지")
        val direct = p2pNetwork
        if (direct != null)
            executor.execute { runCatching { discoverAndConnectLan(direct) } }
        else
            startLanDiscovery()
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
                    contentResolver.openInputStream(uri).use { input ->
                        val bitmap = BitmapFactory.decodeStream(input)
                            ?: error("이미지를 읽을 수 없습니다.")
                        val out = ByteArrayOutputStream()
                        bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
                        bitmap.recycle()
                        ClipboardProtocol.image(ClipboardKind.PNG, out.toByteArray())
                    }
                }.onSuccess(::sendContent)
                    .onFailure { toast("이미지 전송 준비 실패: ${it.message}") }
            }
            return
        }

        toast("텍스트 또는 이미지만 지원합니다.")
    }

    private fun sendContent(content: ClipboardContent) {
        val fast = lan
        if (fast != null) {
            executor.execute {
                runCatching {
                    ClipboardProtocol.encode(content).forEach(fast::sendFrame)
                }.onSuccess {
                    showStatus("Clipboard 전송 완료 · LAN/P2P")
                }.onFailure {
                    fast.close()
                    lan = null
                    enqueueBle(content)
                }
            }
        } else {
            enqueueBle(content)
        }
    }

    private fun enqueueBle(content: ClipboardContent) {
        val g = gatt
        val rxLocal = rx
        if (g == null || rxLocal == null || credential == null) {
            toast("PC Clipboard 연결이 없습니다.")
            return
        }

        if (content.kind != ClipboardKind.TEXT && content.data.size > 512 * 1024) {
            toast("512 KiB 초과 이미지는 LAN/P2P 연결 후 전송해 주세요.")
            return
        }

        val maxGatt = maxOf(20, mtu - 3)
        ClipboardProtocol.encode(content, 16 * 1024).forEach { frame ->
            val transfer = secureRandom.nextInt().toUInt()
            ClipboardProtocol.splitBle(frame, maxGatt, transfer).forEach(bleQueue::add)
        }
        writeNextBle()
    }

    private fun writeNextBle() {
        if (bleWriting) return
        val g = gatt ?: return
        val characteristic = rx ?: return
        val next = bleQueue.poll() ?: run {
            showStatus("Clipboard 전송 완료 · BLE")
            return
        }

        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED)
            return

        bleWriting = true
        val result = if (Build.VERSION.SDK_INT >= 33) {
            g.writeCharacteristic(characteristic, next, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT)
        } else {
            @Suppress("DEPRECATION")
            run {
                characteristic.writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                characteristic.value = next
                if (g.writeCharacteristic(characteristic)) BluetoothStatusCodes.SUCCESS else BluetoothStatusCodes.ERROR_UNKNOWN
            }
        }

        if (result != BluetoothStatusCodes.SUCCESS) {
            bleWriting = false
            showStatus("BLE Clipboard 쓰기 시작 실패: $result")
        }
    }

    private fun applyRemoteClipboard(content: ClipboardContent) {
        main.post {
            val clipboard = getSystemService(android.content.ClipboardManager::class.java)
            when (content.kind) {
                ClipboardKind.TEXT -> {
                    val text = content.data.toString(Charsets.UTF_8)
                    clipboard.setPrimaryClip(ClipData.newPlainText("Con.Phone", text))
                    toast("PC 텍스트를 클립보드에 받았습니다.")
                }
                else -> {
                    runCatching {
                        val bitmap = BitmapFactory.decodeByteArray(content.data, 0, content.data.size)
                            ?: error("수신 이미지를 해석할 수 없습니다.")
                        FileOutputStream(File(cacheDir, ClipboardImageProvider.FILE_NAME)).use { out ->
                            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
                        }
                        bitmap.recycle()
                        val clip = ClipData.newUri(contentResolver, "Con.Phone image", ClipboardImageProvider.URI)
                        clipboard.setPrimaryClip(clip)
                        toast("PC 이미지를 클립보드에 받았습니다.")
                    }.onFailure {
                        toast("이미지 클립보드 적용 실패: ${it.message}")
                    }
                }
            }
        }
    }

    private fun showStatus(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("Con.Phone Companion")
            .setContentText(text)
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
        scanning = false
        lan?.close()
        lan = null
        releaseDirectNetwork()
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
            runCatching { gatt?.close() }
        gatt = null
        executor.shutdownNow()
        super.onDestroy()
    }

    private class LanChannel(
        private val socket: Socket,
        private val credential: Credential,
        private val onFrame: (ByteArray) -> Unit,
        private val onClosed: () -> Unit
    ) {
        private val input = DataInputStream(BufferedInputStream(socket.getInputStream()))
        private val output = BufferedOutputStream(socket.getOutputStream())
        private val sendLock = Any()
        @Volatile private var closed = false

        private val aad = "ConPhone-Clipboard-LAN-v1".toByteArray(Charsets.US_ASCII) + credential.deviceHash

        fun handshake() {
            val hello = "CPHELLO1".toByteArray(Charsets.US_ASCII)
            val clientNonce = ByteArray(16).also(SecureRandom()::nextBytes)
            val signed = hello + credential.deviceHash + clientNonce
            val mac = hmac(credential.secret, signed)
            output.write(signed + mac)
            output.flush()

            val response = ByteArray(56)
            input.readFully(response)
            require(String(response, 0, 8, Charsets.US_ASCII) == "CPREADY1")
            val serverNonce = response.copyOfRange(8, 24)
            val serverMac = response.copyOfRange(24, 56)
            val expected = hmac(
                credential.secret,
                "CPREADY1".toByteArray(Charsets.US_ASCII) + clientNonce + serverNonce
            )
            require(MessageDigest.isEqual(serverMac, expected)) { "LAN authentication failed." }
        }

        fun sendFrame(plaintext: ByteArray) {
            synchronized(sendLock) {
                check(!closed)
                val nonce = ByteArray(12).also(SecureRandom()::nextBytes)
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(
                    Cipher.ENCRYPT_MODE,
                    SecretKeySpec(credential.secret, "AES"),
                    GCMParameterSpec(128, nonce)
                )
                cipher.updateAAD(aad)
                val encrypted = cipher.doFinal(plaintext)
                val length = 12 + encrypted.size

                val prefix = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(length).array()
                output.write(prefix)
                output.write(nonce)
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
                    require(length in 28..(ClipboardProtocol.MAX_IMAGE + 1024 * 1024))
                    val body = ByteArray(length)
                    input.readFully(body)
                    val nonce = body.copyOfRange(0, 12)
                    val encrypted = body.copyOfRange(12, body.size)

                    val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                    cipher.init(
                        Cipher.DECRYPT_MODE,
                        SecretKeySpec(credential.secret, "AES"),
                        GCMParameterSpec(128, nonce)
                    )
                    cipher.updateAAD(aad)
                    onFrame(cipher.doFinal(encrypted))
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
            runCatching { socket.close() }
        }

        private fun hmac(key: ByteArray, data: ByteArray): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(key, "HmacSHA256"))
            return mac.doFinal(data)
        }
    }
}

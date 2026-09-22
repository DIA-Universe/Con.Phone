package universe.dia.conphone

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.ScrollView
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.content.res.ColorStateList
import android.view.WindowInsets

class MainActivity : Activity() {
    private val statusHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private val statusPoll = object : Runnable {
        override fun run() {
            if (hasWindowFocus() && hasBluetoothPermissions()) sendFocusedClipboard(false)
            statusText.text = FriendlyStatus.summary(ClipboardBridgeService.latestStatus)
            statusHandler.postDelayed(this, 500)
        }
    }
    private val textSync = FocusedTextSync()
    private val clipListener = android.content.ClipboardManager.OnPrimaryClipChangedListener {
        if (hasWindowFocus()) sendFocusedClipboard(false)
    }
    private val requestCode = 7001
    private var pendingServiceIntent: Intent? = null

    private lateinit var statusText: TextView
    private val blue = Color.rgb(8, 127, 140)
    private val ink = Color.rgb(26, 28, 32)
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    private fun text(value: String, size: Float = 15f, bold: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(if (bold) ink else Color.rgb(85, 93, 109))
        if (bold) setTypeface(typeface, Typeface.BOLD)
        setPadding(0, dp(4), 0, dp(8))
        setLineSpacing(dp(3).toFloat(), 1f)
    }

    private fun card(root: LinearLayout, title: String, description: String): LinearLayout {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(16), dp(20), dp(16))
            background = GradientDrawable().apply {
                setColor(Color.WHITE)
                cornerRadius = dp(16).toFloat()
                setStroke(dp(1), Color.rgb(225, 230, 238))
            }
        }
        root.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(16) })
        card.addView(text(title, 19f, true))
        if (description.isNotBlank()) card.addView(text(description))
        return card
    }

    private fun button(parent: LinearLayout, label: String, primary: Boolean = false, action: () -> Unit) {
        parent.addView(Button(this).apply {
            text = label
            isAllCaps = false
            textSize = 15f
            minHeight = dp(52)
            background = GradientDrawable().apply { setColor(if (primary) blue else Color.rgb(230, 245, 242)); cornerRadius = dp(12).toFloat() }
            setTextColor(if (primary) Color.WHITE else blue)
            setOnClickListener { action() }
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(6) })
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        capturePendingServiceIntent(intent)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(24), dp(20), dp(24))
        }
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            setBackgroundColor(Color.rgb(243, 245, 249))
            addView(root)
            setOnApplyWindowInsetsListener { view, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
        }
        val brand = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL }
        brand.addView(android.widget.ImageView(this).apply {
            setImageDrawable(packageManager.getApplicationIcon(packageName))
            contentDescription = "LINKO 로고"
        }, LinearLayout.LayoutParams(dp(64), dp(64)).apply { marginEnd = dp(14) })
        brand.addView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(text("LINKO", 30f, true))
            addView(text("복사한 내용을 이어주다", 14f))
        })
        root.addView(brand)
        val home = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val settings = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; visibility = android.view.View.GONE }
        val navigation = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setPadding(0, dp(16), 0, dp(18)) }
        val homeTab = Button(this).apply { text = "홈"; isAllCaps = false }
        val settingsTab = Button(this).apply { text = "설정"; isAllCaps = false }
        fun selectSettings(selected: Boolean) {
            home.visibility = if (selected) android.view.View.GONE else android.view.View.VISIBLE
            settings.visibility = if (selected) android.view.View.VISIBLE else android.view.View.GONE
            homeTab.isSelected = !selected; settingsTab.isSelected = selected
            homeTab.setTextColor(if (selected) ink else blue)
            settingsTab.setTextColor(if (selected) blue else ink)
            homeTab.backgroundTintList = ColorStateList.valueOf(if (selected) Color.WHITE else Color.rgb(230, 245, 242))
            settingsTab.backgroundTintList = ColorStateList.valueOf(if (selected) Color.rgb(230, 245, 242) else Color.WHITE)
        }
        homeTab.setOnClickListener { selectSettings(false) }
        settingsTab.setOnClickListener { selectSettings(true) }
        navigation.addView(homeTab, LinearLayout.LayoutParams(0, dp(52), 1f).apply { marginEnd = dp(8) })
        navigation.addView(settingsTab, LinearLayout.LayoutParams(0, dp(52), 1f))
        root.addView(navigation); root.addView(home); root.addView(settings)
        selectSettings(false)

        val connect = card(home, "PC 연결", "PC에서 LINKO를 실행해 주세요.")
        statusText = text(FriendlyStatus.summary(ClipboardBridgeService.latestStatus), 14f)
        connect.addView(statusText)
        connect.addView(text("선택한 PC에만 자동으로 연결합니다.", 14f))
        button(connect, "연결할 PC 선택") { choosePairedPc() }
        button(connect, "연결") {
            if (hasBluetoothPermissions()) startForegroundService(Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_CONNECT))
            else requestPermissions()
        }

        val send = card(home, "클립보드", "휴대폰에서 복사한 뒤 이 화면을 열면 PC로 보냅니다.")
        button(send, "PC로 보내기", true) { sendFocusedClipboard(true) }
        home.addView(text("이미지는 전달되지 않습니다. 텍스트만 자동으로 동기화합니다.", 15f, true))
        button(send, "다른 앱에서도 자동 복사 설정") {
            startActivity(Intent(this, BackgroundClipboardActivity::class.java))
        }
        home.addView(text("삼성 키보드를 그대로 사용할 수 있습니다. 자동 복사 설정 전에는 이 앱을 열거나 공유 → LINKO로 보내세요.", 13f))

        val options = card(settings, "연결 관리", "")
        button(options, "연결할 PC 선택") { choosePairedPc() }
        button(options, "연결 해제") {
            startForegroundService(Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_DISCONNECT))
        }
        button(options, "앱 권한 설정") {
            startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                android.net.Uri.parse("package:$packageName")))
        }
        @Suppress("DEPRECATION")
        val installedVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "알 수 없음"
        val updates = card(settings, "LINKO $installedVersion", "")
        button(updates, "업데이트 확인") { startActivity(Intent(this, UpdateActivity::class.java)) }
        val help = card(settings, "도움말", "이미지는 전달되지 않습니다. Bluetooth가 연결되면 텍스트 동기화가 자동으로 시작됩니다.")
        button(help, "연결 상세 정보") {
            android.app.AlertDialog.Builder(this).setTitle("연결 상세 정보")
                .setMessage(ClipboardBridgeService.latestStatus).setPositiveButton("닫기", null).show()
        }
        setContentView(scroll)
        // Explain permissions before requesting them; explicit share intents still start immediately.
        ensurePermissionsAndStart()
    }

    override fun onResume() {
        super.onResume()
        if (hasBluetoothPermissions()) ensurePermissionsAndStart()
        statusHandler.post(statusPoll)
        getSystemService(android.content.ClipboardManager::class.java).addPrimaryClipChangedListener(clipListener)
    }

    override fun onPause() {
        statusHandler.removeCallbacks(statusPoll)
        getSystemService(android.content.ClipboardManager::class.java).removePrimaryClipChangedListener(clipListener)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && hasBluetoothPermissions()) sendFocusedClipboard(false)
    }

    private fun sendFocusedClipboard(manual: Boolean) {
        if (!hasWindowFocus()) return
        if (!manual && BackgroundClipboardMonitor.active) return
        if (!hasBluetoothPermissions()) { if (manual) ensurePermissionsAndStart(); return }
        runCatching {
            val clip = getSystemService(android.content.ClipboardManager::class.java).primaryClip
                ?: return
            if (clip.itemCount == 0) return
            val item = clip.getItemAt(0)
            val text = item.text?.toString()
            val ownLabel = clip.description.label?.toString()?.let {
                it.startsWith("LINKO") || it.startsWith("Con.Phone")
            } == true
            val remote = ownLabel && text == ClipboardBridgeService.lastReceivedText
            val queued = textSync.capture(text, remote, manual) { value ->
                AndroidSharedPayloadStore(this).saveText(value)
            }
            if (!queued) {
                if (manual && text.isNullOrEmpty()) android.widget.Toast.makeText(this,
                    "이미지는 전달되지 않습니다. 텍스트를 복사해 주세요.", android.widget.Toast.LENGTH_SHORT).show()
                return
            }
            pendingServiceIntent = Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_DRAIN_SHARED)
            ensurePermissionsAndStart()
        }.onFailure {
            android.widget.Toast.makeText(this, "클립보드 준비 실패: ${it.message}", android.widget.Toast.LENGTH_LONG).show()
        }
    }

    private fun choosePairedPc() {
        if (!hasBluetoothPermissions()) { requestPermissions(); return }
        val adapter = getSystemService(android.bluetooth.BluetoothManager::class.java).adapter
        val devices = adapter?.bondedDevices?.sortedBy { it.name ?: it.address }.orEmpty()
        if (devices.isEmpty()) {
            statusText.text = "휴대폰 Bluetooth 설정에서 먼저 이 PC와 페어링해 주세요."
            return
        }
        android.app.AlertDialog.Builder(this)
            .setTitle("LINKO을 실행한 PC 선택")
            .setItems(devices.map { "${it.name ?: "이름 없는 기기"} · ${it.address.takeLast(5)}" }.toTypedArray()) { _, index ->
                startForegroundService(Intent(this, ClipboardBridgeService::class.java)
                    .setAction(ClipboardBridgeService.ACTION_CONNECT_DEVICE)
                    .putExtra(ClipboardBridgeService.EXTRA_DEVICE_ADDRESS, devices[index].address))
            }
            .setNegativeButton("취소", null)
            .show()
    }

    private fun ensurePermissionsAndStart() {
        if (!hasBluetoothPermissions()) {
            requestPermissions()
            return
        }

        statusText.text = "PC 연결을 요청했어요. 실제 연결 상태는 LINKO 알림에서 확인하세요."
        startForegroundService(
            Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_START)
        )

        pendingServiceIntent?.let { pending ->
            pendingServiceIntent = null
            startForegroundService(pending)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        capturePendingServiceIntent(intent)
        ensurePermissionsAndStart()
    }

    private fun capturePendingServiceIntent(source: Intent?) {
        val action = source?.action ?: return
        if (action != ClipboardBridgeService.ACTION_SEND &&
            action != ClipboardBridgeService.ACTION_SEND_SHARED_TEXT &&
            action != ClipboardBridgeService.ACTION_SEND_SHARED_IMAGE &&
            action != ClipboardBridgeService.ACTION_DRAIN_SHARED &&
            action != ClipboardBridgeService.ACTION_RETRY_P2P)
            return

        pendingServiceIntent =
            Intent(this, ClipboardBridgeService::class.java)
                .setAction(action)
                .also { target ->
                    source.extras?.let { target.putExtras(it) }
                }
    }

    private fun hasBluetoothPermissions(): Boolean =
        checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    private fun hasNearbyWifiPermission(): Boolean =
        if (android.os.Build.VERSION.SDK_INT >= 33)
            checkSelfPermission(Manifest.permission.NEARBY_WIFI_DEVICES) == PackageManager.PERMISSION_GRANTED
        else
            checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    private fun requestP2PPermission() {
        val permission =
            if (android.os.Build.VERSION.SDK_INT >= 33)
                Manifest.permission.NEARBY_WIFI_DEVICES
            else
                Manifest.permission.ACCESS_FINE_LOCATION

        requestPermissions(arrayOf(permission), requestCode + 1)
    }

    private fun requestPermissions() {
        val permissions = mutableListOf(
            Manifest.permission.BLUETOOTH_CONNECT
        )

        // P2P permission is deliberately separate. Denying nearby Wi-Fi/location
        // must never disable BLE clipboard or HID companion connectivity.
        if (android.os.Build.VERSION.SDK_INT >= 33)
            permissions += Manifest.permission.POST_NOTIFICATIONS

        requestPermissions(permissions.toTypedArray(), requestCode)
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == this.requestCode && hasBluetoothPermissions()) {
            ensurePermissionsAndStart()
        } else if (requestCode == this.requestCode) {
            statusText.text = "Bluetooth 권한이 필요해요. 설정의 앱 권한 설정에서 허용해 주세요."
        } else if (requestCode == this.requestCode + 1) {
            if (hasNearbyWifiPermission()) {
                android.widget.Toast.makeText(
                    this,
                    "P2P 고속 전송 권한이 허용되었습니다.",
                    android.widget.Toast.LENGTH_SHORT
                ).show()

                startForegroundService(
                    Intent(this, ClipboardBridgeService::class.java)
                        .setAction(ClipboardBridgeService.ACTION_RETRY_P2P)
                )
            } else {
                android.widget.Toast.makeText(
                    this,
                    "P2P 권한 없이도 BLE 복붙은 계속 사용할 수 있습니다.",
                    android.widget.Toast.LENGTH_SHORT
                ).show()
            }
        }
    }
}

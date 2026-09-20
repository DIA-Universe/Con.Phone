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
    private val requestCode = 7001
    private var pendingServiceIntent: Intent? = null

    private lateinit var statusText: TextView
    private val blue = Color.rgb(37, 99, 235)
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
        card.addView(text(description))
        return card
    }

    private fun button(parent: LinearLayout, label: String, primary: Boolean = false, action: () -> Unit) {
        parent.addView(Button(this).apply {
            text = label
            isAllCaps = false
            textSize = 15f
            minHeight = dp(52)
            backgroundTintList = ColorStateList.valueOf(if (primary) blue else Color.rgb(237, 242, 250))
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
        root.addView(text("Con.Phone", 30f, true))
        root.addView(text("복사한 내용을 PC와 휴대폰에서 함께.", 16f).apply {
            setPadding(0, 0, 0, dp(24))
        })

        val connect = card(root, "1. PC와 연결하기", "PC에서 Con.Phone을 실행하고 Bluetooth로 페어링해 주세요.")
        statusText = text(if (hasBluetoothPermissions()) "Bluetooth 권한이 준비됐어요. PC 연결을 시작하세요." else "처음 연결할 때 주변 기기 권한을 허용해 주세요.", 14f)
        connect.addView(statusText)
        button(connect, "PC 연결 시작", true) { ensurePermissionsAndStart() }

        val send = card(root, "2. 복사한 내용 보내기", "텍스트나 이미지를 복사한 뒤 아래 버튼을 누르세요.")
        button(send, "PC로 보내기", true) {
            pendingServiceIntent = Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_SEND)
            ensurePermissionsAndStart()
        }
        send.addView(text("다른 앱에서는 공유 → Con.Phone을 선택해도 보낼 수 있어요.", 14f))

        val options = card(root, "연결 설정", "같은 Wi-Fi에 연결할 필요는 없어요. 큰 이미지를 빠르게 보내려면 추가 권한을 허용해 주세요.")
        button(options, "빠른 이미지 전송 설정") {
            if (hasNearbyWifiPermission()) {
                android.widget.Toast.makeText(this, "빠른 전송 권한이 허용되어 있어요.", android.widget.Toast.LENGTH_SHORT).show()
            } else requestP2PPermission()
        }
        button(options, "연결 중지") {
            stopService(Intent(this, ClipboardBridgeService::class.java))
            statusText.text = "연결 서비스를 중지했어요. 다시 연결하려면 위 버튼을 누르세요."
        }
        root.addView(text("PC → 휴대폰: PC에서 복사한 뒤 휴대폰 입력으로 전환하세요. 이미지 붙여넣기는 사용하는 앱에 따라 지원 여부가 달라요.", 13f))
        setContentView(scroll)
        // Explain permissions before requesting them; explicit share intents still start immediately.
        if (pendingServiceIntent != null) ensurePermissionsAndStart()
    }

    private fun ensurePermissionsAndStart() {
        if (!hasBluetoothPermissions()) {
            requestPermissions()
            return
        }

        statusText.text = "PC 연결을 요청했어요. 실제 연결 상태는 Con.Phone 알림에서 확인하세요."
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
        checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED &&
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
            Manifest.permission.BLUETOOTH_SCAN,
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
            statusText.text = "Bluetooth 권한이 필요해요. ‘PC 연결 시작’을 눌러 다시 허용해 주세요."
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

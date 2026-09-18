package universe.dia.conphone

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView

class MainActivity : Activity() {
    private val requestCode = 7001

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val padding = (20 * resources.displayMetrics.density).toInt()
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
        }

        root.addView(TextView(this).apply {
            text = "Con.Phone Companion"
            textSize = 26f
        })
        root.addView(TextView(this).apply {
            text = "Windows Con.Phone 0.2.1+와 텍스트/이미지 클립보드를 동기화합니다. " +
                "같은 LAN/P2P가 있으면 자동으로 고속 채널을 사용하고, 없으면 BLE로 fallback합니다.\n\n" +
                "Android → PC 전송은 Android의 클립보드 개인정보 보호 정책 때문에 이 화면의 전송 버튼을 눌러 실행합니다."
            textSize = 16f
        })

        root.addView(Button(this).apply {
            text = "PC 연결 / 연결 유지"
            setOnClickListener { ensurePermissionsAndStart() }
        })

        root.addView(Button(this).apply {
            text = "현재 텍스트/이미지를 PC로 보내기"
            setOnClickListener {
                if (hasBluetoothPermissions()) {
                    startForegroundService(
                        Intent(this@MainActivity, ClipboardBridgeService::class.java)
                            .setAction(ClipboardBridgeService.ACTION_SEND)
                    )
                } else {
                    requestPermissions()
                }
            }
        })

        root.addView(Button(this).apply {
            text = "연결 서비스 중지"
            setOnClickListener {
                startService(
                    Intent(this@MainActivity, ClipboardBridgeService::class.java)
                        .setAction(ClipboardBridgeService.ACTION_STOP)
                )
            }
        })

        root.addView(TextView(this).apply {
            text = "\nPC에서 휴대폰 입력으로 전환할 때 최신 클립보드가 자동으로 전송됩니다. " +
                "이미지 붙여넣기는 대상 Android 앱이 이미지 클립보드 URI를 지원해야 합니다."
            textSize = 14f
        })

        setContentView(root)
        ensurePermissionsAndStart()
    }

    private fun ensurePermissionsAndStart() {
        if (!hasBluetoothPermissions()) {
            requestPermissions()
            return
        }

        startForegroundService(
            Intent(this, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_START)
        )
    }

    private fun hasBluetoothPermissions(): Boolean =
        checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED &&
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED &&
            hasNearbyWifiPermission()

    private fun hasNearbyWifiPermission(): Boolean =
        if (android.os.Build.VERSION.SDK_INT >= 33)
            checkSelfPermission(Manifest.permission.NEARBY_WIFI_DEVICES) == PackageManager.PERMISSION_GRANTED
        else
            checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    private fun requestPermissions() {
        val permissions = mutableListOf(
            Manifest.permission.BLUETOOTH_SCAN,
            Manifest.permission.BLUETOOTH_CONNECT
        )
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            permissions += Manifest.permission.POST_NOTIFICATIONS
            permissions += Manifest.permission.NEARBY_WIFI_DEVICES
        } else {
            permissions += Manifest.permission.ACCESS_FINE_LOCATION
        }

        requestPermissions(permissions.toTypedArray(), requestCode)
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == this.requestCode && hasBluetoothPermissions())
            ensurePermissionsAndStart()
    }
}

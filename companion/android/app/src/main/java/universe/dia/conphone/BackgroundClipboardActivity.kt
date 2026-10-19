package universe.dia.conphone

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.widget.*
import rikka.shizuku.Shizuku

class BackgroundClipboardActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var toggle: Switch
    private val handler = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() { status.text = BackgroundClipboardMonitor.status; handler.postDelayed(this, 750) }
    }
    private val permissionResult = Shizuku.OnRequestPermissionResultListener { code, result ->
        if (code == 7311) {
            if (result == PackageManager.PERMISSION_GRANTED) enable()
            else { toggle.isChecked = false; status.text = "권한을 허용하지 않아 자동 복사를 시작하지 않았습니다." }
        }
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Shizuku.addRequestPermissionResultListener(permissionResult)
        val dp = resources.displayMetrics.density
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding((24*dp).toInt(), (32*dp).toInt(), (24*dp).toInt(), (24*dp).toInt()) }
        fun text(value: String, size: Float = 16f): TextView = TextView(this).apply {
            text = value; textSize = size; setTextColor(Color.rgb(26, 28, 32)); setPadding(0, (10*dp).toInt(), 0, (10*dp).toInt()); root.addView(this)
        }
        fun button(label: String, action: () -> Unit) { root.addView(Button(this).apply { text = label; isAllCaps = false; setOnClickListener { action() } }) }
        text("다른 앱에서도 자동 복사", 24f)
        text("삼성 키보드를 유지합니다. Shizuku의 디버깅 권한을 사용해 다른 앱에서 복사한 텍스트를 페어링한 PC로 자동 전송합니다. 이미지는 전달되지 않습니다.")
        text("1. Shizuku를 설치합니다.\n2. 개발자 옵션의 무선 디버깅으로 Shizuku를 시작합니다.\n3. 아래에서 자동 복사를 켜고 LINKO 권한을 허용합니다.\n\n휴대폰을 재부팅하면 Shizuku를 다시 시작해야 합니다. 초기 무선 디버깅 설정에는 Wi-Fi 연결이 필요합니다. 잠금 중에는 읽기를 멈춥니다.")
        button("Shizuku 설치 / 열기") {
            val launch = packageManager.getLaunchIntentForPackage("moe.shizuku.privileged.api")
            startActivity(launch ?: Intent(Intent.ACTION_VIEW, Uri.parse("https://shizuku.rikka.app/download/")))
        }
        button("무선 디버깅 설정 열기") {
            runCatching { startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)) }
                .onFailure { Toast.makeText(this, "휴대폰 설정에서 개발자 옵션을 먼저 켜 주세요.", Toast.LENGTH_LONG).show() }
        }
        button("설정 방법 보기") { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://shizuku.rikka.app/guide/setup/"))) }
        toggle = Switch(this).apply {
            text = "다른 앱의 텍스트 자동 복사"; textSize = 17f
            isChecked = BackgroundClipboardMonitor.enabled(this@BackgroundClipboardActivity)
            setOnCheckedChangeListener { _, checked ->
                if (!checked) BackgroundClipboardMonitor.setEnabled(this@BackgroundClipboardActivity, false)
                else if (!BackgroundClipboardMonitor.enabled(this@BackgroundClipboardActivity)) {
                    AlertDialog.Builder(this@BackgroundClipboardActivity)
                        .setTitle("텍스트 자동 전송 허용")
                        .setMessage("다른 앱에서 복사하는 텍스트에는 비밀번호 등 민감한 내용이 포함될 수 있습니다. 복사한 텍스트를 연결된 PC로 자동 전송합니다. Shizuku는 강한 디버깅 권한을 제공하지만 LINKO는 이 기능에서 클립보드 읽기에만 사용합니다. 언제든 이 스위치나 Shizuku에서 해제할 수 있습니다.")
                        .setPositiveButton("허용하고 설정") { _, _ -> authorize() }
                        .setNegativeButton("취소") { _, _ -> isChecked = false }
                        .setOnCancelListener { isChecked = false }.show()
                }
            }
        }
        root.addView(toggle)
        status = text(BackgroundClipboardMonitor.status, 14f)
        button("완료") { finish() }
        setContentView(ScrollView(this).apply {
            addView(root)
            setOnApplyWindowInsetsListener { view, insets ->
                val bars = insets.getInsets(android.view.WindowInsets.Type.systemBars())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom); insets
            }
        })
    }
    private fun authorize() {
        runCatching {
            if (!Shizuku.pingBinder()) {
                toggle.isChecked = false
                Toast.makeText(this, "Shizuku 앱에서 먼저 시작해 주세요.", Toast.LENGTH_LONG).show()
            } else if (BackgroundClipboardMonitor.approved()) enable()
            else Shizuku.requestPermission(7311)
        }.onFailure { toggle.isChecked = false; Toast.makeText(this, "Shizuku 실행 상태를 확인해 주세요.", Toast.LENGTH_LONG).show() }
    }
    private fun enable() {
        BackgroundClipboardMonitor.setEnabled(this, true)
        toggle.isChecked = true
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED)
            startForegroundService(Intent(this, ClipboardBridgeService::class.java).setAction(ClipboardBridgeService.ACTION_START))
        else startActivity(Intent(this, MainActivity::class.java))
    }
    override fun onResume() { super.onResume(); handler.post(poll) }
    override fun onPause() { handler.removeCallbacks(poll); super.onPause() }
    override fun onDestroy() { Shizuku.removeRequestPermissionResultListener(permissionResult); super.onDestroy() }
}

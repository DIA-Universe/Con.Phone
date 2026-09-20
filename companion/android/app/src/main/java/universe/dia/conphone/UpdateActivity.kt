package universe.dia.conphone

import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.webkit.CookieManager
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import org.json.JSONObject
import org.json.JSONTokener
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/** Auth stays in GitHub's first-party WebView cookie jar; no token ships in the APK. */
class UpdateActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var status: TextView
    private lateinit var install: Button
    private var downloading = false
    private var generation = 0
    @Volatile private var closed = false
    private val releases = "https://github.com/DIA-Universe/Con.Phone/releases"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(20, 24, 20, 12) }
        status = TextView(this).apply { textSize = 16f; text = "최신 APK 확인 중 · GitHub 로그인이 나오면 로그인해 주세요." }
        root.addView(status)
        install = Button(this).apply {
            text = "다운로드한 업데이트 설치"
            visibility = android.view.View.GONE
            setOnClickListener { installDownloaded() }
        }
        root.addView(install)
        root.addView(Button(this).apply { text = "다시 확인"; setOnClickListener {
            if (!downloading) { generation++; web.visibility = android.view.View.VISIBLE; web.loadUrl(releases) }
        } })
        web = WebView(this).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.mixedContentMode = android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    return request.url.scheme != "https" || request.url.host != "github.com"
                }
                override fun onPageFinished(view: WebView, url: String) {
                    if (closed || downloading) return
                    val uri = Uri.parse(url)
                    if (uri.host == "github.com" && uri.path == "/DIA-Universe/Con.Phone/releases") discover()
                    else status.text = "최신 APK 조회를 위해 이 화면에서 GitHub 로그인을 완료해 주세요."
                }
            }
        }
        root.addView(web, LinearLayout.LayoutParams(-1, 0, 1f))
        setContentView(root)
        web.loadUrl(releases)
    }

    private fun discover() {
        val request = ++generation
        status.text = "최신 릴리스의 APK를 찾는 중…"
        web.evaluateJavascript("""
            (() => {
              window.__conphoneUpdate = null;
              (async () => {
                try {
                  const latest = document.querySelector('a[href^="/DIA-Universe/Con.Phone/releases/tag/"]');
                  if (!latest) throw new Error('릴리스를 찾지 못했습니다. 저장소 접근 권한과 로그인을 확인하세요.');
                  const url = new URL(latest.getAttribute('href').replace('/releases/tag/', '/releases/expanded_assets/'), location.href);
                  if (url.origin !== location.origin || !url.pathname.startsWith('/DIA-Universe/Con.Phone/releases/expanded_assets/')) throw new Error('잘못된 릴리스 주소');
                  const response = await fetch(url.href, {credentials:'same-origin'});
                  if (!response.ok) throw new Error('릴리스 조회 실패: ' + response.status);
                  const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
                  const asset = Array.from(doc.querySelectorAll('a[href]')).find(a => /(?:LINKO-[0-9.]+|ConPhone-Companion-[0-9.]+-debug)[.]apk${'$'}/.test(a.getAttribute('href')));
                  if (!asset) throw new Error('최신 릴리스에 APK가 없습니다.');
                  window.__conphoneUpdate = {url:new URL(asset.getAttribute('href'),location.href).href};
                } catch(e) { window.__conphoneUpdate = {error:String(e.message || e)}; }
              })();
            })();
        """.trimIndent(), null)
        poll(request, 0)
    }

    private fun poll(request: Int, attempt: Int) {
        if (closed || request != generation) return
        web.postDelayed({
            if (closed || request != generation) return@postDelayed
            web.evaluateJavascript("JSON.stringify(window.__conphoneUpdate)") { raw ->
                if (closed || request != generation) return@evaluateJavascript
                val encoded = runCatching { JSONTokener(raw).nextValue() as? String }.getOrNull()
                val result = encoded?.takeUnless { it == "null" }?.let { runCatching { JSONObject(it) }.getOrNull() }
                if (result == null) {
                    if (attempt < 59) poll(request, attempt + 1)
                    else status.text = "업데이트 조회 시간이 초과됐습니다. 다시 확인을 눌러 주세요."
                } else if (result.has("error")) status.text = result.getString("error")
                else download(result.getString("url"))
            }
        }, 500)
    }

    private fun download(assetUrl: String) {
        val uri = Uri.parse(assetUrl)
        if (uri.scheme != "https" || uri.host != "github.com" ||
            uri.path?.startsWith("/DIA-Universe/Con.Phone/releases/download/") != true ||
            uri.lastPathSegment?.matches(Regex("(?:LINKO-[0-9.]+|ConPhone-Companion-[0-9.]+-debug)\\.apk")) != true) {
            status.text = "올바른 LINKO APK 주소가 아닙니다."; return
        }
        if (downloading) return
        downloading = true
        web.visibility = android.view.View.GONE
        status.text = "최신 APK 다운로드 중…"
        val cookie = CookieManager.getInstance().getCookie(assetUrl)
        Thread {
            val directory = File(filesDir, "updates").apply { mkdirs() }
            val partial = File(directory, "download.part")
            runCatching {
                var current = URL(assetUrl)
                var downloaded = false
                for (redirect in 0..5) {
                    check(!closed) { "화면을 닫아 다운로드를 취소했습니다." }
                    check(current.protocol == "https" && current.host in setOf("github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com")) { "허용되지 않은 다운로드 서버" }
                    val connection = current.openConnection() as HttpURLConnection
                    connection.instanceFollowRedirects = false
                    connection.connectTimeout = 15000
                    connection.readTimeout = 20000
                    // Never forward GitHub login cookies to an asset CDN.
                    if (current.host == "github.com" && cookie != null) connection.setRequestProperty("Cookie", cookie)
                    try {
                        val code = connection.responseCode
                        if (code in setOf(301, 302, 303, 307, 308)) {
                            current = URL(current, connection.getHeaderField("Location") ?: error("다운로드 주소 없음"))
                            continue
                        }
                        check(code == 200) { "APK 다운로드 실패: HTTP $code" }
                        connection.inputStream.use { input -> partial.outputStream().use { output ->
                            val buffer = ByteArray(32768)
                            var total = 0L
                            while (true) {
                                check(!closed) { "다운로드 취소" }
                                val count = input.read(buffer)
                                if (count < 0) break
                                total += count
                                check(total <= 100L * 1024 * 1024) { "APK 크기 제한 초과" }
                                output.write(buffer, 0, count)
                            }
                        } }
                        downloaded = true
                        break
                    } finally { connection.disconnect() }
                }
                check(downloaded) { "다운로드 리디렉션 한도 초과" }
                @Suppress("DEPRECATION")
                val candidate = packageManager.getPackageArchiveInfo(partial.path, PackageManager.GET_SIGNING_CERTIFICATES)
                    ?: error("APK 파일을 읽을 수 없습니다.")
                @Suppress("DEPRECATION")
                val installed = packageManager.getPackageInfo(packageName, PackageManager.GET_SIGNING_CERTIFICATES)
                check(candidate.packageName == packageName) { "다른 앱의 APK입니다." }
                val expected = installed.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
                val actual = candidate.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
                check(!expected.isNullOrEmpty() && expected == actual) { "APK 서명이 기존 앱과 다릅니다." }
                if (candidate.longVersionCode <= installed.longVersionCode) {
                    partial.delete()
                    runOnUiThread { if (!closed) status.text = "현재 앱이 최신 버전입니다 (${installed.versionName})." }
                } else {
                    val target = File(directory, "companion.apk")
                    partial.copyTo(target, overwrite = true)
                    partial.delete()
                    runOnUiThread { if (!closed) {
                        status.text = "${candidate.versionName} 다운로드 및 서명 확인 완료"
                        install.visibility = android.view.View.VISIBLE
                    } }
                }
            }.onFailure { error ->
                partial.delete()
                runOnUiThread { if (!closed) status.text = "업데이트 실패: ${error.message}" }
            }
            runOnUiThread { downloading = false }
        }.start()
    }

    private fun installDownloaded() {
        if (!packageManager.canRequestPackageInstalls()) {
            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            status.text = "업데이트 설치를 허용한 후 돌아와 설치 버튼을 눌러 주세요."
            return
        }
        startActivity(Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(ClipboardImageProvider.APK_URI, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        })
    }

    override fun onDestroy() {
        closed = true
        generation++
        web.destroy()
        super.onDestroy()
    }
}

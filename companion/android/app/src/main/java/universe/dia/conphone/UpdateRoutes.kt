package universe.dia.conphone

import java.net.URI

/** Public URLs only. GitHub authentication is owned by the WebView cookie jar. */
internal object UpdateRoutes {
    const val base = "/DIA-Universe/Con.Phone/releases"
    const val latest = "https://github.com$base/latest"
    private fun trusted(url: String): URI? = runCatching { URI(url) }.getOrNull()
        ?.takeIf { it.scheme == "https" && it.host == "github.com" && it.userInfo == null && it.port == -1 }
    fun isReleasePage(url: String): Boolean {
        val path = trusted(url)?.path?.trimEnd('/') ?: return false
        return path == base || path == "$base/latest" ||
            (path.startsWith("$base/tag/") && path.removePrefix("$base/tag/").isNotEmpty())
    }
    fun isApk(url: String): Boolean {
        val path = trusted(url)?.path ?: return false
        return path.startsWith("$base/download/") && path.substringAfterLast('/').matches(
            Regex("(?:LINKO-[0-9.]+|ConPhone-Companion-[0-9.]+-debug)\\.apk"))
    }
}

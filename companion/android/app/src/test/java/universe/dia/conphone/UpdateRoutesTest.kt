package universe.dia.conphone

import org.junit.Assert.*
import org.junit.Test

class UpdateRoutesTest {
    @Test fun followsLatestRedirectAndOldReleaseList() {
        for (suffix in listOf("", "/", "/latest", "/tag/v1.0.2", "/tag/v1.0.2?expanded=true"))
            assertTrue(UpdateRoutes.isReleasePage("https://github.com${UpdateRoutes.base}$suffix"))
        assertFalse(UpdateRoutes.isReleasePage("https://github.com/login"))
        assertFalse(UpdateRoutes.isReleasePage("https://github.com/other/repo/releases"))
    }
    @Test fun acceptsBothAssetNamesAndRejectsOtherOrigins() {
        val prefix = "https://github.com${UpdateRoutes.base}/download/v1.0.2/"
        assertTrue(UpdateRoutes.isApk(prefix + "LINKO-1.0.2.apk"))
        assertTrue(UpdateRoutes.isApk(prefix + "ConPhone-Companion-1.0.2-debug.apk"))
        assertFalse(UpdateRoutes.isApk(prefix + "LINKO.exe"))
        assertFalse(UpdateRoutes.isApk(prefix.replace("github.com", "github.com.evil.test") + "LINKO-1.0.2.apk"))
        assertFalse(UpdateRoutes.isApk(prefix.replace("https:", "http:") + "LINKO-1.0.2.apk"))
    }
}

package universe.dia.conphone

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager

class AutoConnectReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return
        runCatching {
            context.startForegroundService(Intent(context, ClipboardBridgeService::class.java)
                .setAction(ClipboardBridgeService.ACTION_START))
        }.onFailure {
            android.util.Log.w("LINKO", "Automatic connection could not start; open LINKO to retry", it)
        }
    }
}

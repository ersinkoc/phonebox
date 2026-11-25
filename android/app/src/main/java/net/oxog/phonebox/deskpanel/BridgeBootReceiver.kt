package net.oxog.phonebox.deskpanel

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** After a restart the clipboard service would otherwise stay off until the panel is opened, with the switch still on. */
class BridgeBootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED) ClipboardService.sync(context)
    }
}

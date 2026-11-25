package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * "Send to Mac" appears in the text-selection menu, the Share sheet and the Quick Settings tile list, a long press on a
 * PhoneBox tile comes to the app, and the app starts at boot, only while the phone's Phone bridge switch is on: for
 * everyone else the phone behaves as before. All of these are android:enabled="false" in the manifest. The panel's
 * overview button and the service's notification open the Phone bridge screen, which is where the switch is.
 */
object BridgeEntryPoints {
    /** The alias with the tile long-press filter (AndroidManifest.xml); an alias has no class to name. */
    const val TILE_PREFERENCES = "net.oxog.phonebox.deskpanel.BridgeTilePreferences"

    /** Safe to call on every resume: a component already in the wanted state is not touched (each change is written to disk). */
    fun setEnabled(context: Context, on: Boolean) {
        val pm = context.packageManager
        val names = mutableListOf(SendToMacActivity::class.java.name, BridgeBootReceiver::class.java.name, TILE_PREFERENCES)
        // by name, and only from Android 7: the tile class extends TileService, which older systems do not have
        if (Build.VERSION.SDK_INT >= 24) names += "net.oxog.phonebox.deskpanel.SendClipboardTile"
        for (name in names) {
            val component = ComponentName(context.packageName, name)
            val current = pm.getComponentEnabledSetting(component)
            // off goes back to the manifest's default (disabled); a component that was never switched on is left as it is
            if (on && current != PackageManager.COMPONENT_ENABLED_STATE_ENABLED)
                pm.setComponentEnabledSetting(component, PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP)
            else if (!on && current == PackageManager.COMPONENT_ENABLED_STATE_ENABLED)
                pm.setComponentEnabledSetting(component, PackageManager.COMPONENT_ENABLED_STATE_DEFAULT, PackageManager.DONT_KILL_APP)
        }
    }
}

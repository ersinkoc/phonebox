package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.RequiresApi

/**
 * Quick Settings tile (Edit tiles › PhoneBox, #180): tapping it closes the shade and brings the panel to the front.
 * It only opens the app, so it has no state of its own.
 */
// RequiresApi: the app may install below Android 7 (#184), where Quick Settings tiles don't exist
@RequiresApi(Build.VERSION_CODES.N)
class PanelTile : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = Tile.STATE_INACTIVE
            updateTile()
        }
    }

    // Android 14 wants a PendingIntent; the Intent form is the only one before it
    @SuppressLint("StartActivityAndCollapseDeprecated")
    override fun onClick() {
        val open = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
        if (Build.VERSION.SDK_INT >= 34)
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        else @Suppress("DEPRECATION") startActivityAndCollapse(open)
    }
}

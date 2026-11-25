package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.RequiresApi

/** A Quick Settings tile that sends the phone's clipboard to the Mac. It has no state of its own. */
// RequiresApi: the app may install below Android 7, where Quick Settings tiles don't exist (like PanelTile)
@RequiresApi(Build.VERSION_CODES.N)
class SendClipboardTile : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = Tile.STATE_INACTIVE
            updateTile()
        }
    }

    // Android 14 wants a PendingIntent; the Intent form is the only one before it
    @SuppressLint("StartActivityAndCollapseDeprecated")
    override fun onClick() {
        val read = Intent(this, ReadClipboardActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34)
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, read, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        else @Suppress("DEPRECATION") startActivityAndCollapse(read)
    }
}

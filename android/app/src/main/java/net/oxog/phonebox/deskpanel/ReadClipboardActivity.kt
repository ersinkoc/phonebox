package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Bundle

/**
 * Sends what is on the phone's clipboard. Android lets only the app in front read the clipboard, so this see-through
 * activity waits until it has window focus, reads once, sends, and closes. The tile and the notification button open it.
 */
class ReadClipboardActivity : Activity() {
    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val clip = (getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip
        // the item's own text only: coerceToText would read a content: URI's stream on this (main) thread, and a clip that is a file or an
        // image is not something to send to the Mac's clipboard anyway
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
        // password managers mark what they copy as sensitive (Android 13 named the extra; some makers set it earlier): such a clip is never sent
        val sensitive = Build.VERSION.SDK_INT >= 24 && clip?.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true
        BridgeSend.send(this, text, sensitive) { finish() }
    }
}

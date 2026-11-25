package net.oxog.phonebox.deskpanel

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import java.text.NumberFormat

/**
 * "Send to Mac" in the text-selection menu (next to Copy, from Android 6) and in the Share sheet. See-through, with a
 * small confirmation: the start of the text, how much more there is, and a Send to Mac button. Any installed app can start
 * this activity with any text (the chooser starts it on the sender's behalf, so a caller check cannot tell), and that text
 * would land on the Mac's clipboard, ready to be pasted into a terminal. So nothing is sent until the owner has seen it and
 * tapped Send: the preview cannot be padded to hide the end (see [BridgeClip.preview]), it says when something is not shown,
 * and the button only works after [BridgeClip.CONFIRM_DELAY_MS], so a tap that was meant for the app underneath cannot confirm.
 */
class SendToMacActivity : Activity() {
    private var dialog: AlertDialog? = null
    private val main = Handler(Looper.getMainLooper())
    private var enableSend: Runnable? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // exported, so any app can start it: extras that cannot be read (a Parcelable class this app does not have) must not
        // crash the process, which also runs the panel and the clipboard service. They count as nothing to send.
        val text = try {
            when (intent?.action) {
                Intent.ACTION_PROCESS_TEXT -> if (Build.VERSION.SDK_INT >= 23) intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString() else null
                Intent.ACTION_SEND -> intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() // styled text is not a String
                else -> null
            }
        } catch (_: RuntimeException) { null }
        // the switch is off, or the text is empty or too long: say so without asking first (nothing is sent either way)
        if (!BridgeSend.sendable(this, text, sensitive = false) { finish() }) return
        val clip = text!!
        val night = resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
        val preview = BridgeClip.preview(clip)
        val message = if (preview.moreChars == 0) preview.shown else {
            val number = NumberFormat.getIntegerInstance()
            preview.shown + "\n\n" + getString(R.string.bridge_confirm_more, number.format(preview.moreChars), number.format(preview.moreLines))
        }
        val theme = if (night) android.R.style.Theme_Material_Dialog_Alert else android.R.style.Theme_Material_Light_Dialog_Alert
        dialog = AlertDialog.Builder(this, theme)
            .setTitle(getString(R.string.bridge_confirm_title))
            .setMessage(message)
            .setPositiveButton(getString(R.string.bridge_send_to_mac)) { _, _ -> BridgeSend.send(this, clip, sensitive = false) { finish() } }
            .setNegativeButton(getString(R.string.pair_negative), null)
            .setOnDismissListener { finish() } // Cancel, Back or a tap outside: nothing is sent
            .show()
            .apply {
                // as the pairing dialog: the default all-caps follows the phone's language rules, not the text as written
                listOf(AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE).forEach { getButton(it)?.isAllCaps = false }
                // a tap that reaches the button through another app's window drawn on top does not count (tapjacking)
                window?.decorView?.filterTouchesWhenObscured = true
                // and one that lands as the dialog appears (a finger already on its way to something else) cannot confirm it
                getButton(AlertDialog.BUTTON_POSITIVE)?.let { send ->
                    send.isEnabled = false
                    enableSend = Runnable { send.isEnabled = true }.also { main.postDelayed(it, BridgeClip.CONFIRM_DELAY_MS) }
                }
            }
    }

    override fun onDestroy() {
        // a configuration change recreates the activity, and the dialog with it, from the same intent; this one must not leak
        enableSend?.let { main.removeCallbacks(it) }
        enableSend = null
        dialog?.setOnDismissListener(null)
        dialog?.dismiss()
        dialog = null
        super.onDestroy()
    }
}

package net.oxog.phonebox.deskpanel

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.widget.Toast

/**
 * Sends one text to the Mac's clipboard and says what happened in a toast: at once from the tile and the notification
 * button (they read the clipboard themselves), after the owner's tap on the confirmation for the selection menu and the
 * Share sheet. The text is held only for the request; nothing is stored or logged.
 */
object BridgeSend {
    /**
     * Whether [text] could be sent now. When not, it has already said why (a toast, or the Phone bridge screen when the
     * phone's switch is off) and called [done]: the confirmation is not shown for a text that would not be sent anyway.
     */
    fun sendable(context: Context, text: String?, sensitive: Boolean, done: () -> Unit = {}): Boolean {
        val app = context.applicationContext
        val main = Handler(Looper.getMainLooper())
        fun refuse(message: Int): Boolean { main.post { Toast.makeText(app, message, Toast.LENGTH_SHORT).show(); done() }; return false }
        if (!BridgePrefs.clipboardEnabled(app)) {
            // the phone's own switch is off: take the person to it instead of silently sending
            app.startActivity(Intent(app, BridgeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            main.post { done() }
            return false
        }
        return when (BridgeClip.skipReason(text, sensitive)) {
            BridgeClip.Skip.NOTHING -> refuse(R.string.bridge_send_nothing)
            BridgeClip.Skip.TOO_LARGE -> refuse(R.string.bridge_send_too_large)
            null -> true
        }
    }

    fun send(context: Context, text: String?, sensitive: Boolean, done: () -> Unit = {}) {
        if (!sendable(context, text, sensitive, done)) return
        val app = context.applicationContext
        val main = Handler(Looper.getMainLooper())
        val clip = text!!
        Thread {
            val link = AgentLink(app)
            val outcome = try {
                when (BridgeClient(link).sendClipboard(clip)) {
                    SendResult.OK -> R.string.bridge_sent
                    SendResult.OFF -> R.string.bridge_send_off
                    SendResult.TOO_LARGE -> R.string.bridge_send_too_large
                    SendResult.UNREACHABLE -> R.string.bridge_send_unreachable
                    // the request went out and no answer came back: the text may be on the Mac, so this is not called a failure
                    SendResult.UNKNOWN -> R.string.bridge_send_unknown
                    SendResult.PAIR_AGAIN -> R.string.bridge_pair_again
                }
            } finally {
                link.close()
            }
            // the two messages that ask the owner to look or act get the long toast
            val length = if (outcome == R.string.bridge_pair_again || outcome == R.string.bridge_send_unknown) Toast.LENGTH_LONG else Toast.LENGTH_SHORT
            main.post { Toast.makeText(app, outcome, length).show() }
        }.start()
        // the see-through window goes away now: a full-screen touchable window must not freeze the app below while the request runs
        main.post { done() }
    }
}

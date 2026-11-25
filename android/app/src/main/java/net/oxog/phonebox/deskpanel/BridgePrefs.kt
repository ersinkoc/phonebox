package net.oxog.phonebox.deskpanel

import android.content.Context

/**
 * The phone's own bridge switches ("Phone bridge" screen). A feature runs only when this switch and the one on the
 * computer (Settings › Phone bridge) are both on; the computer's switch is enforced by the agent.
 */
object BridgePrefs {
    private fun prefs(context: Context) = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)

    fun clipboardEnabled(context: Context): Boolean = prefs(context).getBoolean("clipboard", false)

    fun setClipboardEnabled(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("clipboard", on).apply()
    }

    /** The bridge's pieces (Send to Mac entry points, boot start) are switched on: set by a sync with the switch on, cleared by the clean-up. */
    fun live(context: Context): Boolean = prefs(context).getBoolean("live", false)

    fun setLive(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("live", on).apply()
    }

    @Volatile var clipboardState: ClipboardState = ClipboardState.STOPPED

    /** The last thing that went wrong starting the service (a class name, never clipboard text), shown on the screen. */
    @Volatile var lastError: String? = null
}

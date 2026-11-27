package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Test

/** The bridge waits for the agent as long as the panel's request does, within bounds (#720). */
class PanelReadTimeoutTest {
    @Test
    fun aPanelDeadlineSetsTheReadTimeout() {
        assertEquals(15000, AgentLink.panelReadTimeout("15000"))
        assertEquals(15000, AgentLink.panelReadTimeout(" 15000 "))
    }

    @Test
    fun withoutAUsableDeadlineTheDefaultStays() {
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout(null))
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout(""))
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout("soon"))
    }

    @Test
    fun theDeadlineIsKeptWithinBounds() {
        // a short panel deadline never makes the bridge give up sooner than it always did
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout("2500"))
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout("-1"))
        assertEquals(AgentLink.MAX_REQUEST_READ_TIMEOUT, AgentLink.panelReadTimeout("600000"))
    }

    @Test
    fun usbLeavesTheWifiAttemptTimeInsideTheDeadline() {
        // a 15 s deadline: a stalled tunnel gives up at 11 s, so Wi-Fi still has 4 s
        assertEquals(11000, AgentLink.usbReadTimeout(15000))
        // the usual 4 s request keeps its 4 s over USB, as before
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.usbReadTimeout(AgentLink.REQUEST_READ_TIMEOUT))
        assertEquals(AgentLink.REQUEST_READ_TIMEOUT, AgentLink.usbReadTimeout(6000))
    }
}

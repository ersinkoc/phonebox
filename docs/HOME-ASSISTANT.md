# Home Assistant dashboard widget

The **Home Assistant dashboard** widget shows a Home Assistant dashboard as it is, with its own cards, on the panel.
The phone opens the dashboard itself, from your Home Assistant server. (The **Home Assistant** widget is the other way
round: the agent reads chosen entities with your token and the panel draws them its own way.)

The widget needs Android 7 or later on the phone. On Android 5 and 6 it says so instead of opening the dashboard.

## Setup in five steps

1. **PhoneBox:** in **Settings › General › Home Assistant**, enter the address and a long-lived access token
   (Home Assistant: your profile › **Security** › **Long-lived access tokens**). The agent uses the token on the
   computer to list the dashboards in the widget's settings; it never goes to the phone.
2. **Home Assistant, two add-ons** from **Settings › Marketplace** (HACS on Home Assistant 2026.10 and older):
   - [PhoneBox embed](https://github.com/dancho12/ha-phonebox-embed) fits a dashboard to the panel: a 12 × 6 grid, no
     background, no padding. It works with any theme and changes nothing outside the panel.
   - [kiosk-mode](https://github.com/NemesisRE/kiosk-mode) hides the header and the sidebar. The widget asks for that
     itself (`?kiosk` in the address), so on the computer the dashboard keeps them.
3. **Home Assistant, one setting:** add this to `configuration.yaml` and restart Home Assistant. Without it Home
   Assistant refuses to be shown inside another app, and the widget stays empty.
   ```yaml
   http:
     use_x_frame_options: false
   ```
   If `configuration.yaml` already has an `http:` section, add the second line to it instead: a second `http:`
   would replace the first, with its settings. The setting is for all of Home Assistant: any web page, not only the
   panel, may then show Home Assistant in a frame.
4. **A dashboard for the panel:** in Home Assistant, a new dashboard, view type **Sections**, the view's
   **Max number of section columns** at **1**, and one section with your cards. In its raw configuration add
   ```yaml
   phonebox:
     columns: 12
     rows: 6
   ```
   A card's size (**Layout** in its settings) is then in panel cells: 6 × 2 is half the width and a third of the
   height. The PhoneBox embed README has more, with pictures.

5. **The widget, and a sign-in on the phone once:** add the **Home Assistant dashboard** widget to a page and pick
   the dashboard in its settings. The phone shows Home Assistant's sign-in page in the widget; sign in there once, and
   Home Assistant keeps the phone signed in. Best with a Home Assistant user of its own for the panel, without
   administrator rights (Home Assistant: **Settings › People › Add person**, allow it to sign in). Typing is easier
   in portrait: switch **Settings › Screen › Orientation** for the sign-in and back afterwards.

## The widget's settings

- **Dashboard and view**: the dashboards and views Home Assistant lists, or **Another path…** for any
  `dashboard/view`, for example `lovelace/0`. Under the list, **The phone opens** shows the exact address.
- **Hide Home Assistant's header and sidebar** (on by default; needs kiosk-mode).
- **Home Assistant address for the phone**: only when the phone reaches Home Assistant at another address than the
  computer, for example when Settings has `localhost` (Home Assistant on the same computer): enter the computer's
  address on the network, such as `http://192.168.1.10:8123`, or a name the phone can find. The phone uses this
  address alone. Without it, the phone takes the address from Settings; with a `.local` name such as
  `homeassistant.local` it tries the name first and then the IP addresses the name has on the computer, and uses the
  first that answers.
- **No card background** (on by default): the dashboard's own cards are the widget.

Until the server answers, the widget says so and tries again every 15 seconds. When the server answers again after
an outage, the dashboard is reloaded.

## How the sign-in works

The phone signs in to Home Assistant itself, like a browser: Home Assistant's sign-in page shows in the widget once,
and Home Assistant keeps the session in the frame's own storage on the phone. The token from Settings stays on the
computer. The dashboard runs as the user who signed in, so a user without administrator rights keeps the panel from
changing Home Assistant's settings.

Instead of a password, Home Assistant's
[trusted networks](https://www.home-assistant.io/docs/authentication/providers/#trusted-networks) can let the phone in
by its IP address; that needs the phone to keep the same address on your network (a reservation in the router).

## Plain http

Home Assistant on a home network is often plain `http`. The panel allows that: it is served over `http` inside the
app (it never goes to the network), so the dashboard and its pictures are not blocked as mixed content.

Over plain `http` the sign-in (the password) and the session travel unencrypted on the home network, as in any
browser: a device that can watch that traffic can read them. Home Assistant over https, or trusted networks instead
of a password, avoids sending the password; the user without administrator rights limits what a session can do.

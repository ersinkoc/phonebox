# PhoneBox in the GNOME top bar

A GNOME Shell extension that puts PhoneBox in the top bar: CPU and the Claude session quota at a glance, and the
full panel, page tabs included, one click away.

```
 Activities          Sun 00:17          CPU 16% · Claude 8%
                                       ┌──────────────────────┐
                                       │ Home  AI  Sessions … │
                                       │   PhoneBox panel     │
                                       └──────────────────────┘
```

- **Left click** shows the panel under the indicator; clicking again, pressing **Esc** or clicking anywhere else
  hides it.
- **Right click** opens the settings page (<http://127.0.0.1:8765>).
- The text is dimmed and reads "PhoneBox" while the agent is not running.

The panel is the [desktop widget](../gnome-widget) in its `--dropdown` mode. The extension starts it hidden at
login, so the first click opens a panel that has already loaded, and shows and hides it over D-Bus (the
`org.gtk.Actions` every Gtk.Application exports) instead of minimizing it, so minimize animations never play.

## Install

Needs GNOME Shell 45–48, a running PhoneBox agent, and GTK 3 with WebKitGTK 4.1 for Python
(`sudo apt install python3-gi gir1.2-webkit2-4.1` on Debian/Ubuntu).

```sh
./install.sh            # installs the widget and the extension, enables it
```

Then **log out and back in**: GNOME on Wayland only picks up a newly installed extension at login.

`./install.sh --uninstall` removes the extension.

## Troubleshooting

- `journalctl --user -f /usr/bin/gnome-shell` shows the extension's errors.
- The panel itself can be tried without the extension: `phonebox-widget --dropdown --show-at 1400 40`.

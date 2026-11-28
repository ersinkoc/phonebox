# PhoneBox on a QNAP NAS (Container Station)

[Back to setup](SETUP.md)

Run the Linux agent in QNAP Container Station, so a phone or tablet on the desk stays connected over Wi-Fi without
a computer left on. The phone can be paired with the NAS and with your computer, and switch between them.

## What works on a NAS

The agent is the Linux one (see [What works where](SETUP.md#what-works-where)), seen from inside a container:

- CPU, memory and network are the NAS's. The disk list and the processes are those the container sees.
- Widgets that read a service over the network work as on any computer: Home Assistant, news feeds, calendars, webhooks
  and the like. Weather comes from the phone itself.
- Command widgets and shortcuts run `bash` inside the container, as its `phonebox` user.
- Nothing that needs a desktop or a program the container lacks: Now playing, volume, keys, the trackpad, the GitHub
  widget (`gh`), Claude Code and Codex quotas and sessions. Those stay with the agent on your computer.

## Requirements

- A QNAP NAS with Container Station on an x64 or ARM64 CPU. The Linux builds need glibc, so the container is Debian,
  not Alpine.
- The PhoneBox app on the phone, installed from the [release page](https://github.com/ersinkoc/phonebox/releases/latest).
- The phone and the NAS on the same network, or both on [Tailscale](https://tailscale.com).
- SSH on the NAS (**Control Panel › Network & File Services › Telnet / SSH**) with TCP port forwarding allowed, and an
  account that may log in over SSH (an administrator on QTS 5.2): the settings page is reached only through an SSH
  tunnel.

## 1. Create the application

In Container Station open **Applications › Create**, name it `phonebox`, paste the file below and click **Create**.

```yaml
services:
  phonebox:
    # The Linux agent needs glibc: Debian, not Alpine
    image: debian:bookworm-slim
    container_name: phonebox
    # The Wi-Fi port 8766 is the NAS's own and the pairing QR code holds the NAS's LAN address.
    # The settings page stays on the NAS's 127.0.0.1:8765; reach it with an SSH tunnel, never a proxy.
    network_mode: host
    restart: unless-stopped
    volumes:
      # The parent folder, not .../app: setup.sh renames app to app.old while it installs
      - /share/Container/phonebox/share:/home/phonebox/.local/share/PhoneBox
      - /share/Container/phonebox/config:/home/phonebox/.config/PhoneBox
      # Docker widget only. Gives the agent root-equivalent access to the NAS: read docs/QNAP.md first
      # - /var/run/docker.sock:/var/run/docker.sock
    command:
      - /bin/bash
      - -lc
      - |
        set -e
        if ! command -v curl >/dev/null || ! command -v unzip >/dev/null; then
          export DEBIAN_FRONTEND=noninteractive
          apt-get update
          apt-get install -y --no-install-recommends curl ca-certificates unzip
        fi
        # setup.sh refuses to run as root, so setup and the agent run as the user phonebox
        id -u phonebox >/dev/null 2>&1 || useradd -m -s /bin/bash phonebox
        mkdir -p /home/phonebox/.local/share/PhoneBox /home/phonebox/.config/PhoneBox
        chown -R phonebox:phonebox /home/phonebox
        BIN=/home/phonebox/.local/share/PhoneBox/app/PhoneBoxAgent
        if [ ! -x "$$BIN" ]; then
          su - phonebox -c 'curl -fsSL https://raw.githubusercontent.com/ersinkoc/phonebox/main/setup.sh | bash -s -- --yes --no-phone --no-autostart --no-extras --no-start'
        fi
        if [ ! -x "$$BIN" ]; then
          echo "PhoneBoxAgent was not installed in /home/phonebox/.local/share/PhoneBox/app; see the setup output above." >&2
          exit 1
        fi
        exec su - phonebox -c "exec $$BIN"
```

The first start takes a minute or two: the container installs `curl`, `ca-certificates` and `unzip`, then runs
[`setup.sh`](../setup.sh), the same installer as in [Quick start](SETUP.md#quick-start), which downloads the latest
release and checks it against the release's SHA-256 checksums. Add `--version 0.9.32` to its options to install a
given release instead. Settings and pairings are kept in `/share/Container/phonebox/config`, the agent and its log
(`agent.log`) in `/share/Container/phonebox/share`.

**Restart to apply** and the agent's own updates end the agent; `restart: unless-stopped` then starts the container
again, which starts the agent. That takes a few seconds.

## 2. Open the settings

The settings page has no password because only programs on the same computer can reach it, here the NAS. Reach it
through an SSH tunnel from your computer:

```sh
ssh -L 8765:127.0.0.1:8765 <nas-user>@<nas-ip>
```

Then open <http://127.0.0.1:8765> in a browser on your computer, and close the SSH session when you are done. Keep the
local port 8765: the agent refuses changes from any other address, so with another port the page loads but cannot
save. If PhoneBox also runs on your computer it holds that port; quit it while the tunnel is open.

Do not publish port 8765 or put a proxy in front of it (an nginx container, for example). That gives every device on
the network the settings page, and with it a command widget that runs any command on the NAS
([SECURITY.md](../SECURITY.md)).

## 3. Pair the phone over Wi-Fi

The phone talks to the agent's own Wi-Fi port, 8766: HTTPS with the agent's certificate and a key that only paired
phones have ([Wi-Fi & Tailscale](SETUP.md#wi-fi--tailscale)).

1. In the settings, open **General › Wi-Fi & Tailscale** and turn on **Let the phone connect without the USB cable**.
2. Click **Save**, then **Restart to apply**, wait for the container to start again and reload the page. The line
   next to **Pair a phone over Wi-Fi** says "Listening on port 8766".
3. Click **Pair a phone over Wi-Fi**, scan the second QR code with the phone's camera, open the link, tap **Open in
   PhoneBox** and confirm the code the window also shows.

### QuFirewall

If QuFirewall is on, allow TCP port 8766 from your local network:

1. **QuFirewall › Firewall Profiles**, **Edit** on the active profile.
2. **Add Rule**: Permission `Allow`, Interface `All` (or your LAN adapter), Source your LAN subnet (for example
   `192.168.1.0` / `255.255.255.0`), Protocol `TCP`, Service Port `Custom` › `8766`.
3. Make sure the rule is on and above any `Deny` rule, then click **Apply**.

For a phone outside your network use Tailscale rather than forwarding port 8766 on the router. Tailscale runs on the
NAS (QNAP's App Center has it), not in this container. The agent offers a Tailscale address in the pairing QR code
only when the NAS has one on a network interface (in the `100.64.0.0/10` range, as `ip addr` over SSH shows); a
Tailscale running in userspace networking mode adds none. With QuFirewall on, add a second rule like the one above
with Source `100.64.0.0` / `255.192.0.0`.

## Docker widget

Mounting `/var/run/docker.sock` gives the agent root-equivalent access to the NAS: whatever can use the Docker socket
can start a container with the NAS's whole file system in it, and through a command widget anyone who can edit the
agent's settings can too. Mount it only if you want the Docker widget. The widget also runs the `docker` command,
which this container does not have, and the `phonebox` user needs permission to use the socket; the file above sets up
neither.

## Connections

The first start reaches `deb.debian.org` for the packages, and `raw.githubusercontent.com` and GitHub for `setup.sh`
and the release. With `--yes`, setup also takes its default answer to installing adb; with no `sudo` in the container
it downloads Google's platform tools from `dl.google.com` on x64 (adb is not used here). The agent's own connections
are listed in [PRIVACY.md](../PRIVACY.md).

## Troubleshooting

- **The container stops at once or restarts again and again:** read its log in Container Station. Setup needs the
  internet on the first start; `PhoneBoxAgent was not installed` means `setup.sh` failed.
- **`ssh -L` connects, but the page does not load** (`channel … open failed: administratively prohibited`): the NAS's
  SSH server does not allow port forwarding. Some QTS versions turn it off; the setting belongs to QTS, not PhoneBox.
  Allow TCP forwarding in the NAS's SSH server (`AllowTcpForwarding yes` in its sshd configuration) and restart SSH from
  the Telnet / SSH page. Some QTS versions rewrite that configuration when SSH restarts, so open the tunnel again and
  check that the page loads before pairing. Without forwarding the settings page cannot be reached, and the agent cannot be paired, so
  this setup cannot be used on that NAS.
- **The phone cannot reach port 8766:** check that **Wi-Fi & Tailscale** says "Listening on port 8766", that the phone
  is on the same network (or on Tailscale, see above), and that the QuFirewall rules are on and above any `Deny` rule.
- **Reinstall the agent:** stop the container, delete `/share/Container/phonebox/share/app` and start it again; setup
  then installs the latest release, or the one given with `--version` (remove that option from the compose file
  first to get the latest). Settings and pairings stay.

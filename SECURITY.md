# Security policy

Please report vulnerabilities through [GitHub private vulnerability reporting](https://github.com/ersinkoc/phonebox/security/advisories/new).
Include the affected version, operating system, connection mode (USB or paired Wi-Fi), reproduction steps and impact.
Do not include access tokens, pairing keys, calendar URLs or personal session transcripts.

Security fixes target the latest release. Install the latest agent and Android app together when a fix changes both sides.

PhoneBox can run shortcuts and control local applications. Keep the agent on loopback, its key-checked USB tunnel or its
authenticated, pinned-TLS Wi-Fi connection. Custom HTML widgets are sandboxed; they must use the documented widget messaging interface.

Do not put a proxy in front of the local port (`127.0.0.1:8765`) to reach it from another device. It has no password
because only programs on this computer can reach it, and its `Host` and `Origin` checks are what keep web pages away
from it. A proxy that serves it on the network, or rewrites those headers, lets every device on that network, and every
web page opened on any of them, change the settings, add a command widget that runs any command, and answer Claude Code
permission prompts. To use PhoneBox from another device, pair it over Wi-Fi or Tailscale instead.

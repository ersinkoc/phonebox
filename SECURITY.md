# Security policy

Please report vulnerabilities through [GitHub private vulnerability reporting](https://github.com/paulgeorge92/phonebox/security/advisories/new).
Include the affected version, operating system, connection mode (USB or paired Wi-Fi), reproduction steps and impact.
Do not include access tokens, pairing keys, calendar URLs or personal session transcripts.

Security fixes target the latest release. Install the latest agent and Android app together when a fix changes both sides.

PhoneBox can run shortcuts and control local applications. Keep the agent on loopback or its authenticated, pinned-TLS
Wi-Fi connection. Custom HTML widgets are sandboxed; they must use the documented widget messaging interface.

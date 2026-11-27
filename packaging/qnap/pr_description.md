# Pull Request Draft & Submission Checklist

Use this file to submit your QNAP Container Station guide to [ersinkoc/phonebox](https://github.com/ersinkoc/phonebox) in line with [CONTRIBUTING.md](https://github.com/ersinkoc/phonebox/blob/main/CONTRIBUTING.md).

## Suggested repository placement

Place the two contribution files in a dedicated folder so they stay self-contained and easy to review:

* `packaging/qnap/docker-compose.yml` (or `docs/qnap/docker-compose.yml`)
* `packaging/qnap/README.md` (or `docs/qnap/README.md`)

Optionally, you can also add a single bullet under **Other ways to install** in `docs/SETUP.md` pointing to the guide:

```markdown
- **QNAP NAS (Container Station):** run the Linux agent 24/7 in Docker for an always-on Wi-Fi panel without keeping a desktop PC running — see [packaging/qnap/README.md](../packaging/qnap/README.md).
```

> Note per [CONTRIBUTING.md](https://github.com/ersinkoc/phonebox/blob/main/CONTRIBUTING.md): Leave `CHANGELOG.md` and version numbers to the maintainer, and keep the PR focused on this single addition.

## Ready-to-paste GitHub Pull Request

**Suggested PR Title:**

`docs: add QNAP Container Station Docker Compose setup and Wi-Fi pairing guide`

**Suggested PR Description:**

```markdown
### What this changes for the user
Adds a self-contained Docker Compose configuration and step-by-step guide (`packaging/qnap/docker-compose.yml` and `packaging/qnap/README.md`) for running the PhoneBox Linux agent 24/7 on a QNAP NAS using Container Station and pairing an Android phone or tablet over Wi-Fi.

### Why
Users who want an always-on desk panel (clocks, weather, calendars, timers, Home Assistant, RSS feeds, webhooks, Plex, or NAS/Docker stats) don't always want to leave a desktop PC powered on. Running the Linux agent in QNAP Container Station keeps the panel online continuously over Wi-Fi without changing any agent or Android app code.

### Security & privacy considerations
- **No changes to PhoneBox's security model:** The agent itself still binds settings strictly to `127.0.0.1:8765` and serves Wi-Fi over pinned TLS on port `8766`.
- **Optional LAN settings proxy (`phonebox-proxy`):** Documented as strictly local-network-only, with explicit guidance to stop the proxy container once pairing and layout edits are finished (or remove it and use an SSH tunnel instead).
- **Non-root execution:** The container creates a dedicated `phonebox` user to satisfy `setup.sh`'s non-root requirement and mounts the parent `.local/share/PhoneBox` directory so `setup.sh` and self-updates can rename `app` cleanly.
- **Docker socket off by default:** `/var/run/docker.sock` is commented out unless the user explicitly opts in.
- **No new PhoneBox telemetry or outbound endpoints:** Only standard `debian:bookworm-slim` `apt` packages and PhoneBox's official `setup.sh` release download (with SHA-256 verification).

### How this was tested
Tested by hand on a QNAP NAS running QTS (`Linux 5.10.60-qnap`, x64) with Container Station:
- Created the `phonebox` Application with `docker-compose.yml`
- Verified `setup.sh` downloaded and SHA-256-verified PhoneBox `0.9.31` (`linux-x64`) as non-root user `phonebox`
- Accessed the settings UI via `phonebox-proxy` on LAN port `18765`
- Enabled **Wi-Fi & Tailscale**, restarted the agent, added a QuFirewall LAN allow rule for TCP port `8766`, and paired an Android tablet over Wi-Fi
- Confirmed the tablet stayed connected and functional after stopping the PC agent

### Checklist
- [x] Documentation and config only (no agent or Android code modified)
- [x] Left `CHANGELOG.md` and version numbers untouched
- [x] Licensed under the MIT License per `CONTRIBUTING.md`
```

## Step-by-step: how to submit on GitHub

1. **Fork the repository:** Go to [github.com/ersinkoc/phonebox](https://github.com/ersinkoc/phonebox) and click **Fork**.
2. **Create a branch** in your fork (for example, `docs/qnap-container-station`).
3. **Upload the files:** Add `docker-compose.yml` and `README.md` under `packaging/qnap/` (you can use GitHub's **Add file › Create new file** or **Upload files** button directly in the browser—no local `git` required).
4. **Open a Pull Request:** Click **Contribute › Open pull request**, paste the title and description above, and submit.
5. **Watch for automated review:** Per [CONTRIBUTING.md](https://github.com/ersinkoc/phonebox/blob/main/CONTRIBUTING.md), CodeRabbit will review the PR automatically—address or reply to any comments so the conversations can be resolved before merge.

# GitHub Copilot CLI status

PhoneBox can show whether a [GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli) session is
**Working**, has **Turn finished** or **Needs attention** (a permission prompt or a question waiting for you), and how
long ago that changed. Copilot CLI runs a small script from its hooks, and the script sends the state to the agent's
[webhook API](GUIDE.md#webhooks). A **Pushed value** widget shows it; the **Notifications** widget keeps the recent
changes as a short history.

It is read-only: nothing on the phone can answer Copilot CLI or send it anything.

Requirements: Copilot CLI with hooks, and Python 3.8 or later where Copilot CLI runs (no extra packages). It works
wherever Copilot CLI runs: on the computer with the PhoneBox agent (Windows, macOS or Linux) and in WSL2 on that
computer.

## What it sends

Only the state: `Working`, `Turn finished` or `Needs attention`. The agent adds the time it arrived. The script reads
the hook input (which holds your prompt, folder and session) and throws it away without parsing it, so prompts,
replies, folder paths, session names, tool names, arguments and output never leave Copilot CLI. Webhooks are inbound
only; see the Webhooks row in [PRIVACY.md](../PRIVACY.md).

With several sessions open, the widget shows the latest change from any of them.

## 1. Install the script

Copy [copilot-cli-bridge.py](copilot-cli-bridge.py) into Copilot CLI's user hooks folder:

```sh
mkdir -p ~/.copilot/hooks
curl -fsSL -o ~/.copilot/hooks/copilot-cli-bridge.py \
     https://raw.githubusercontent.com/ersinkoc/phonebox/main/docs/copilot-cli-bridge.py
```

Copilot CLI only loads `*.json` files from that folder, and a hook may always read the folder it was loaded from, so
the script keeps working in sandboxed sessions. If you set `COPILOT_HOME`, use `$COPILOT_HOME/hooks` instead.

## 2. Add the hooks

Create `~/.copilot/hooks/phonebox.json`:

```json
{
  "version": 1,
  "hooks": {
    "userPromptSubmitted": [
      { "type": "command", "bash": "python3 ~/.copilot/hooks/copilot-cli-bridge.py working", "timeoutSec": 5 }
    ],
    "agentStop": [
      { "type": "command", "bash": "python3 ~/.copilot/hooks/copilot-cli-bridge.py finished --notify", "timeoutSec": 5 }
    ],
    "notification": [
      { "type": "command", "matcher": "permission_prompt|elicitation_dialog",
        "bash": "python3 ~/.copilot/hooks/copilot-cli-bridge.py attention --notify", "timeoutSec": 5 }
    ]
  }
}
```

`--notify` also shows a notification on the phone; remove it from the entries where you only want the widget to
change. Then start a new Copilot CLI session.

When Copilot CLI runs on Windows, give each entry a `powershell` field next to `bash`, for example
`"powershell": "python \"$HOME/.copilot/hooks/copilot-cli-bridge.py\" working"`.

## 3. Connect it to PhoneBox

| Where Copilot CLI runs | `PHONEBOX_URL` | Webhook key |
|---|---|---|
| The computer with the PhoneBox agent (Windows, macOS, Linux) | `http://127.0.0.1:8765` (the default) | Not needed |
| WSL2 with mirrored networking | `http://127.0.0.1:8765` (the default) | Not needed |
| WSL2 with NAT networking (WSL's default) | `https://<windows-host>:8766` | Needed |

In WSL2, `wslinfo --networking-mode` prints `mirrored` or `nat`. Mirrored networking (Windows 11 22H2 or later: set
`networkingMode=mirrored` under `[wsl2]` in `%USERPROFILE%\.wslconfig`, then run `wsl --shutdown`) is the simplest:
nothing to set up.

In NAT mode `127.0.0.1` in WSL is WSL's own loopback, not Windows, so the script goes through the agent's Wi-Fi port,
over HTTPS and with the webhook key. That key opens only the webhook endpoints, never the phone API.

1. In the PhoneBox settings, turn on **Wi-Fi & Tailscale** under **General**, click **Save**, then **Restart to apply**
   at the top.
2. Under **Webhooks**, click **Copy key**. In WSL, save it in a file only you can read (paste, press Enter, then
   Ctrl+D):

   ```sh
   (umask 077; d="${XDG_CONFIG_HOME:-$HOME/.config}/phonebox"; mkdir -p "$d"; touch "$d/webhook-key"; chmod 600 "$d/webhook-key"; cat > "$d/webhook-key")
   ```

3. On Windows, open `http://127.0.0.1:8765/api/lan` and copy the `fingerprint` value. The agent's certificate is
   self-signed; the script sends the key only after checking the certificate against this fingerprint.
4. Add to `~/.bashrc` (or the profile of the shell you start Copilot CLI from), then open a new terminal:

   ```sh
   export PHONEBOX_URL="https://$(ip route show default | awk '{print $3; exit}'):8766"   # Windows as seen from WSL
   export PHONEBOX_CERT_SHA256="<the fingerprint>"
   ```

| Variable | Meaning |
|---|---|
| `PHONEBOX_URL` | Where the agent is. `http://` is accepted for `127.0.0.1` only |
| `PHONEBOX_WEBHOOK_KEY` | The webhook key, for `https://` only. Read from `PHONEBOX_WEBHOOK_KEY_FILE` when not set |
| `PHONEBOX_WEBHOOK_KEY_FILE` | The file holding the key (default `$XDG_CONFIG_HOME/phonebox/webhook-key`, or `~/.config/phonebox/webhook-key` without it) |
| `PHONEBOX_CERT_SHA256` | The agent's certificate fingerprint, required for `https://` |
| `PHONEBOX_PUSH_KEY` | The key of the pushed value (default `copilot`), for example one per computer. Set the same key in the **Pushed value** widget's options, since the example page reads `copilot` |

Try it by hand; `--check` reports what happened instead of staying silent:

```sh
python3 ~/.copilot/hooks/copilot-cli-bridge.py finished --notify --check
```

## 4. Add the page

In the settings, open **Pages**, click **Import** and choose [copilot-cli-page.json](copilot-cli-page.json), then
save. It holds one page with a **Pushed value** widget (key `copilot`) and a **Notifications** widget. You can also add
the two widgets to a page of your own.

## Troubleshooting

| Problem | What to try |
|---|---|
| The widget says it has no data yet | Run the `--check` command above; it names the problem. Restart Copilot CLI after adding the hook file |
| `--check` works but Copilot CLI sends nothing | With the session sandbox on, hooks run inside it: allow the connection (and reading `~/.config/phonebox`, or set `PHONEBOX_WEBHOOK_KEY` instead) in the sandbox settings. See [Using hooks](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks) |
| NAT mode: `timed out` | Windows Firewall may treat the WSL network as public while PhoneBox is allowed on private networks only. Mirrored networking avoids this |
| `does not match PHONEBOX_CERT_SHA256` | The agent has a new certificate: copy the fingerprint from `/api/lan` again |
| **Needs attention** stays after you answered | It changes with the next state: when the turn ends or you send a new prompt. If you deny the request, Copilot CLI cancels the turn without running `agentStop`, so it changes with your next prompt |
| **Working** disappears during a very long turn | A state of **Working** expires after 4 hours, so a session closed mid-turn does not show it forever |

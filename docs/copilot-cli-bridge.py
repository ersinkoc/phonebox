#!/usr/bin/env python3
"""
GitHub Copilot CLI -> PhoneBox: shows whether a Copilot CLI session is working, has finished its turn or needs your
attention, through the webhook API. Copilot CLI runs it from its hooks; see docs/COPILOT-CLI.md.

    copilot-cli-bridge.py working|finished|attention [--notify] [--check]

    --notify   also show a notification on the phone
    --check    for trying it by hand: report what happened and exit 1 on failure (hooks stay silent and exit 0)

Only the state reaches PhoneBox, which shows how long ago it arrived. The hook input on stdin (prompt, folder, session)
is read and thrown away, never parsed. Only the Python standard library is used.

Settings (environment variables):
    PHONEBOX_URL                http://127.0.0.1:8765 (default; Windows, macOS, Linux, WSL2 with mirrored networking)
                                or https://<windows-host>:8766 (WSL2 with NAT networking, through the Wi-Fi port)
    PHONEBOX_WEBHOOK_KEY        the webhook key, needed for https only ...
    PHONEBOX_WEBHOOK_KEY_FILE   ... or a file holding it (default ~/.config/phonebox/webhook-key)
    PHONEBOX_CERT_SHA256        the SHA-256 fingerprint of the agent's certificate, needed for https
    PHONEBOX_PUSH_KEY           the key of the pushed value (default copilot)
"""
import hashlib, hmac, http.client, ipaddress, json, os, re, ssl, sys
from pathlib import Path
from urllib.parse import urlsplit

TITLE = "Copilot CLI"
STATES = {
    "working": ("Working", "info"),
    "finished": ("Turn finished", "ok"),
    "attention": ("Needs attention", "warn"),
}
# A session closed in the middle of a turn never reports "finished"; let "Working" expire instead of staying forever.
WORKING_TTL = 4 * 3600
TIMEOUT = 2


class BridgeError(Exception):
    pass


def setting(name, default=""):
    return os.environ.get(name, "").strip() or default


def webhook_key():
    key = setting("PHONEBOX_WEBHOOK_KEY")
    if key:
        return key
    config = Path(setting("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    path = Path(setting("PHONEBOX_WEBHOOK_KEY_FILE", str(config / "phonebox" / "webhook-key"))).expanduser()
    try:
        key = path.read_text(encoding="utf-8").strip()
    except OSError:
        key = ""
    if not key:
        raise BridgeError(f"https needs the webhook key: set PHONEBOX_WEBHOOK_KEY or put it in {path}")
    return key


def target():
    url = urlsplit(setting("PHONEBOX_URL", "http://127.0.0.1:8765"))
    host = url.hostname or ""
    if url.scheme not in ("http", "https") or not host or url.path not in ("", "/") or url.query or url.username:
        raise BridgeError("PHONEBOX_URL must look like http://127.0.0.1:8765 or https://<windows-host>:8766")
    port = url.port or (443 if url.scheme == "https" else 80)
    if url.scheme == "http":
        # Plain HTTP only reaches the agent on this machine, which needs no key; the key never travels unencrypted.
        try:
            loopback = host == "localhost" or ipaddress.ip_address(host).is_loopback
        except ValueError:
            loopback = False
        if not loopback:
            raise BridgeError("other machines are reached over https (port 8766); http is for 127.0.0.1 only")
        return host, port, None, None
    pin = re.sub(r"[\s:]", "", setting("PHONEBOX_CERT_SHA256")).lower()
    if not re.fullmatch(r"[0-9a-f]{64}", pin):
        raise BridgeError("https needs PHONEBOX_CERT_SHA256: the fingerprint from http://127.0.0.1:8765/api/lan on the PC")
    return host, port, pin, webhook_key()


def post(dest, path, body):
    host, port, pin, key = dest
    headers = {"Content-Type": "application/json"}
    # http.client rather than urllib: no proxy from the environment and no redirects, so the key goes to this host only.
    if pin:
        # The agent's certificate is self-signed, so it is checked against the pinned fingerprint instead of a CA,
        # before the key is sent. A fresh connection per request keeps every request behind that check.
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        conn = http.client.HTTPSConnection(host, port, timeout=TIMEOUT, context=ctx)
        conn.connect()
        der = conn.sock.getpeercert(binary_form=True) or b""
        if not hmac.compare_digest(hashlib.sha256(der).hexdigest(), pin):
            conn.close()
            raise BridgeError(f"the certificate of {host}:{port} does not match PHONEBOX_CERT_SHA256; nothing was sent")
        headers["Authorization"] = "Bearer " + key
    else:
        conn = http.client.HTTPConnection(host, port, timeout=TIMEOUT)
    try:
        conn.request("POST", path, body=json.dumps(body).encode("utf-8"), headers=headers)
        resp = conn.getresponse()
        resp.read()
    finally:
        conn.close()
    if resp.status >= 300:
        raise BridgeError(f"POST {path} answered HTTP {resp.status}")


def main(argv):
    args = [a for a in argv if a not in ("--notify", "--check")]
    check, notify = "--check" in argv, "--notify" in argv
    try:
        # inside the try: a hook whose stdin cannot be read still stays silent and exits 0
        if sys.stdin is not None and not sys.stdin.isatty():
            sys.stdin.buffer.read()
        if len(args) != 1 or args[0] not in STATES:
            raise BridgeError("usage: copilot-cli-bridge.py working|finished|attention [--notify] [--check]")
        push_key = setting("PHONEBOX_PUSH_KEY", "copilot")
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,64}", push_key):
            raise BridgeError("PHONEBOX_PUSH_KEY may only use letters, digits, '.', '_' and '-' (up to 64)")
        value, level = STATES[args[0]]
        dest = target()
        state = {"title": TITLE, "value": value, "level": level}
        if args[0] == "working":
            state["ttl"] = WORKING_TTL
        post(dest, "/api/push/" + push_key, state)
        if notify:
            post(dest, "/api/notify", {"title": TITLE, "text": value, "level": level})
    except (BridgeError, OSError, ValueError, http.client.HTTPException) as e:
        # A hook must never get in the way of Copilot CLI: when PhoneBox is not running, the hook just does nothing.
        if check:
            print(f"copilot-cli-bridge: {e}", file=sys.stderr)
            return 1
        return 0
    if check:
        print(f"copilot-cli-bridge: sent \"{value}\"" + (" and a notification" if notify else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

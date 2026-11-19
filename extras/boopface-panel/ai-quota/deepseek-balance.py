#!/usr/bin/env python3
"""Push the DeepSeek account balance to the PhoneBox panel.

DeepSeek has no usage/quota endpoint like Anthropic's, only the account balance
(GET /user/balance). This maps it onto PhoneBox's "Pushed value" widget.

Run it on a timer; the value survives agent restarts, so the panel keeps the
last known balance even if this script stops.

  key lookup:  $DEEPSEEK_API_KEY  ->  ~/.dsh/.credentials.yaml
  panel:       http://127.0.0.1:8765/api/push/deepseek
"""
import json
import os
import re
import sys
import urllib.request

AGENT = "http://127.0.0.1:8765"
PUSH_KEY = "deepseek"


def api_key():
    key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
    if key:
        return key
    creds = os.path.expanduser("~/.dsh/.credentials.yaml")
    try:
        with open(creds, encoding="utf-8") as fh:
            m = re.search(r"DEEPSEEK_API_KEY:\s*(\S+)", fh.read())
    except OSError as exc:
        sys.exit(f"no DeepSeek key: {exc}")
    if not m:
        sys.exit("no DEEPSEEK_API_KEY in ~/.dsh/.credentials.yaml")
    return m.group(1)


def balance(key):
    req = urllib.request.Request(
        "https://api.deepseek.com/user/balance",
        headers={"Authorization": f"Bearer {key}", "Accept": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=25) as resp:
        return json.load(resp)


def main():
    try:
        data = balance(api_key())
    except Exception as exc:  # network, key, JSON: report on the panel instead of dying silently
        push({"title": "DeepSeek", "value": "—", "sub": "balance unavailable",
              "level": "error", "items": [{"label": "Error", "value": str(exc)[:60], "level": "error"}]})
        sys.exit(1)

    infos = {b.get("currency"): b for b in data.get("balance_infos", [])}
    cur = "USD" if "USD" in infos else ("CNY" if "CNY" in infos else None)
    if not cur:
        push({"title": "DeepSeek", "value": "—", "sub": "no balance reported", "level": "warn"})
        sys.exit(1)

    b = infos[cur]
    total = float(b.get("total_balance", 0))
    topped = float(b.get("topped_up_balance", 0))
    granted = float(b.get("granted_balance", 0))
    available = bool(data.get("is_available"))
    symbol = "$" if cur == "USD" else "¥"

    level = "ok" if available and total > 0.5 else ("warn" if available and total > 0 else "error")
    status = "available" if available else "unavailable"

    payload = {
        "title": "DeepSeek",
        "value": f"{symbol}{total:,.2f}",
        "unit": "",
        "sub": f"{cur} · {status}",
        "level": level,
        "items": [
            {"label": "Balance", "value": f"{symbol}{total:,.2f}", "level": level},
            {"label": "Topped up", "value": f"{symbol}{topped:,.2f}"},
            {"label": "Granted", "value": f"{symbol}{granted:,.2f}"},
            {"label": "Status", "value": status, "level": "ok" if available else "error"},
        ],
    }
    push(payload)
    print(f"DeepSeek {cur} {total:.2f} ({status}) -> panel")


def push(payload):
    req = urllib.request.Request(
        f"{AGENT}/api/push/{PUSH_KEY}",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=10) as resp:
        resp.read()


if __name__ == "__main__":
    main()

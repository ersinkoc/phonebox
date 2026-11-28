# MQTT

PhoneBox can connect to an MQTT broker (Mosquitto, Home Assistant's add-on, Zigbee2MQTT, a cloud broker) from the
computer. This is the first step: the agent holds the connection and reports its state. Subscribing to topics so their
values reach the panel, a widget and publish actions come in later steps (issue #732).

The connection is made by the agent on the computer, never by the phone. The phone never gets the broker's address, user
name, password or topics.

## Set it up

1. In **Settings › General › MQTT**, enter the broker's **Address** and **Port** (1883 for a plain connection, 8883
   for TLS, the usual WebSocket ports are 8083 and 8084 or whatever the broker says).
2. Choose the **Transport**: TCP, or WebSocket (then the **WebSocket path**, usually `/mqtt`).
3. If the broker wants a login, enter the **User name** and the **Password**.
4. Press **Test**: it connects once with what is on the page (even before you save) and disconnects.
5. Turn on **Connect to an MQTT broker** and save. The line next to **Test** then shows whether the connection holds.

It is off by default. While it is on, the agent keeps trying: after a failure it waits 5 seconds, then 10, 20, 40 and
60 seconds between tries.

## Options

| Setting | What it does |
|---|---|
| Client ID | The id the broker knows the agent by. Empty: `phonebox-` and eight characters derived from this computer's name. **Test** adds `-test`, so it never drops the real connection (a broker disconnects the older of two clients with the same id) |
| Keep-alive | How often the agent pings the broker when it is quiet (5 to 600 seconds, default 60) |
| Clean session | The broker forgets this client's state when it disconnects (on by default) |
| Last will topic and message | What the broker publishes if the agent disappears without saying goodbye. An empty topic means no last will. The topic has no wildcards |
| Retain the last will | **Off by default.** A retained message is delivered again to every device that connects or restarts, so a retained command can be acted on again long after it was meant. Leave it off unless you are sure |

The last will's QoS (0, 1 or 2) is `willQos` in `config.json` (default 0).

## Certificates

With **Use TLS** on, the broker's certificate is always checked. By default, and with a CA file, it must be a server
certificate that is valid for the address you entered. There is no setting that skips the check.

A broker with its own certificate (a self-signed one on your network, or one from your own certificate authority) is
trusted in one of two ways:

- **CA certificate file**: the PEM (or DER) file of the authority that signed it. The certificate must chain to it and
  still match the address.
- **Certificate fingerprint (SHA-256)**: pins one certificate instead of checking a chain and a name. Enter the
  fingerprint of the broker's own certificate, as `openssl x509 -noout -fingerprint -sha256 -in broker.crt`
  prints it (colons and capitals are fine). Only that exact certificate is accepted, whatever its name or expiry date,
  so enter the new fingerprint when the broker's certificate is renewed. When both are set, the fingerprint is used.

The password and the other settings travel in clear text without TLS. Use TLS whenever the broker is not on a network
you trust.

## The password

The password lives in `config.json` on the computer, like the Home Assistant token. The settings page can write it but
never gets it back: it shows only that one is saved. Leave the field empty to keep it, type a new one to replace it, or
press **Remove the saved password**. The saved password stays only for the broker it was saved for (the same address, port,
WebSocket path, transport, TLS setting and user name), in **Test** and when you save: change any of them and you type the
password again, so it is never sent somewhere you did not enter it.

It is not in what the phone receives, in the log or in the status.

## The status

`GET /api/mqtt/status` (on the computer only, not over Wi-Fi) answers with the state (`off`, `connecting`, `connected` or
`error`), the time of the last message in milliseconds since 1970 (`null` until one arrives) and, when something is wrong,
a kind of error: `dns`, `refused`, `timeout`, `tls`, `auth`, `protocol`, `config` (the address is not a plain host name or IP address, or the CA file cannot be read) or `other`. Never the address, the user
name, a topic or the error's own text, which usually names the broker.

## What the broker sees

A public broker sees this computer's address, the client id, the user name and everything the agent sends it. See
[PRIVACY.md](../PRIVACY.md).

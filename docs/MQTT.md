# MQTT

PhoneBox can connect to an MQTT broker (Mosquitto, Home Assistant's add-on, Zigbee2MQTT, a cloud broker) from the
computer and put the values it receives into the panel's pushed values. This is the second step (issue #732): the
agent holds the connection, reports its state and subscribes to the topics you list. A widget for the connection's
state and publish actions come in later steps. The agent publishes nothing itself; the only message that can be
published is the last will, if you set one, and the broker publishes it when the connection is lost.

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
60 seconds between tries. The wait starts over at 5 seconds once a connection has held for a minute or the settings
change. A broker that accepts the login and then drops the connection (a duplicate client ID, an access list) is
therefore retried more and more slowly, and the log notes it once.

## Subscriptions

In **Settings › General › MQTT**, under **Subscriptions**, each row puts the messages of a topic into one pushed value:

| Field | What it does |
|---|---|
| Topic | The topic to subscribe to. `+` stands for one level (`home/+/temp`) and `#` for everything below (`sensors/#`, last level only). Shared subscriptions (`$share/…`) are not supported |
| Key | The pushed value's key: 1 to 64 letters, digits, `.`, `_` or `-`, the same as a [webhook's](GUIDE.md#webhooks) |
| JSON field | Optional. For a JSON message, the field to take, such as `temperature`, `sensor.temp` or `list.0` (a number picks an array item). Empty: the whole message |
| Unit | Optional. Stored with a number or a text as `{"value": 21.5, "unit": "°C"}`, which the Pushed value widget shows next to the value |

The value is then in the push store like one posted to `/api/push/<key>`: a **Pushed value** widget
with that key shows it, a rule can compare it ("a pushed value is above…", "has had no update for N seconds") and
`GET /api/push` lists it. Nothing else of MQTT reaches the phone, and no topic does.

- **One row, one key.** A wildcard never makes keys: every message that matches a row updates that row's key, the
  newest one winning. A busy broker (`#`) therefore cannot fill the push store, which holds 200 keys in all, webhooks
  included. A row needs a valid topic and a key, a key can be used once, and there are at most 32 rows. The settings page marks a row that breaks one of these and does not save until it is fixed (the agent drops such a row too).
- **At most one update a second per key.** A sensor that publishes ten times a second changes its key once a second,
  with the newest value. Every message that does get through updates the value's time, even when the value is the
  same, so a rule that watches for a silent sensor still works.
- **What is stored.** A number, `true` or `false` stays one and anything else is a text, like a webhook's body. A JSON
  object or array without a field is kept as it is (so `{"value": 21.5, "unit": "°C", "title": "Hall"}` works as a
  Pushed value would; a number `ttl` in it expires the value, as in a webhook's), and a unit is added only to a number or a text. A message that has no usable value writes
  nothing: an empty one (a retained message being cleared), one that is not text, one larger than 64 KB, one that is not
  JSON when a field is set, or one without that field.
- **Retained messages** arrive right after the agent subscribes, so a value is there as soon as the connection is up.
  A change in the settings connects again, which subscribes again.
- **Removed rows.** With **Clean session** on (the default) the broker forgets the topics when the connection ends. With it off, the
  broker keeps a client's subscriptions between connections, so the agent unsubscribes from a topic you remove when it
  connects again. After the agent itself has been restarted it does not know the old topics: the broker may keep sending
  them until the session ends (turn **Clean session** on and connect once to clear them). Those messages are ignored, but
  they still move the time of the last message in the status.
- The agent subscribes at QoS 0 (a value to show, not a command). If the broker refuses a subscription (an access list),
  the log says how many, never which; the connection stays up.
- **Test** subscribes to nothing.

## Options

| Setting | What it does |
|---|---|
| Client ID | The id the broker knows the agent by. Empty: `phonebox-` and eight characters derived from this computer's name. **Test** adds `-test`, so it never drops the real connection (a broker disconnects the older of two clients with the same id) |
| Keep-alive | How often the agent pings the broker when it is quiet (5 to 600 seconds, default 60) |
| Clean session | The broker forgets this client's state when it disconnects (on by default) |
| Last will topic and message | What the broker publishes if the agent disappears without saying goodbye. An empty topic means no last will. The topic has no wildcards |
| Retain the last will | **Off by default.** A retained message is delivered to a device whenever it subscribes, which for most boards is right after it connects or restarts, so a retained command can be acted on again long after it was meant. Leave it off unless you are sure |

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

A public broker sees this computer's address, the client id, the user name, the topics you subscribe to and everything
the agent sends it. See [PRIVACY.md](../PRIVACY.md).

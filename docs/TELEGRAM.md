# Telegram notifications

PhoneBox can show new Telegram messages on the phone, with a vibration and a sound of your choice for each chat. It
signs in to your Telegram account as one more device (like Telegram Desktop or the web version), so Windows
notifications for Telegram Desktop are not needed. Telegram Desktop can keep its own notifications off.

Each chat's notification settings in Telegram are followed:

| In Telegram | On the phone |
|---|---|
| Chat muted (for a while or forever) | No notification, unless you are mentioned or the chat is marked **Even when muted** in PhoneBox |
| "Notify without sound" for the chat, or a message sent silently | The notification shows, without sound or vibration |
| "Show previews" off | Only the chat name, not the text |
| The chat's sound: none, default, or one of your own ringtones | No sound, a short tone, or your ringtone played on the phone |

Settings changed in Telegram (on any device) apply within a few seconds. PhoneBox reads messages only to announce
them: nothing is marked as read, nothing is sent, and your online status is not changed.

## 1. Create a Telegram application

1. Open [my.telegram.org](https://my.telegram.org), sign in with your phone number and choose **API development tools**.
2. Fill in the form (any app title and short name, for example `PhoneBox` / `phonebox`; platform **Desktop**) and click
   **Create application**.
3. Copy the **api_id** (a number) and the **api_hash**.

## 2. Sign in from PhoneBox

1. Open the PhoneBox settings page, choose **General** and find the **Telegram** card.
2. Turn the switch on, paste the **api_id** and **api_hash**, enter your phone number in international format
   (`+380…`) and click **Save**.
3. Click **Sign in**. Telegram sends a code to your other Telegram devices (or by SMS). Type it into the box that appears
   and click **Send**.
4. If your account has two-step verification, enter your cloud password next.

The status then shows **Signed in as …**. The login is kept in `telegram.session` next to `config.json` and survives
restarts. **Sign out** ends it in Telegram too. You can also end it from Telegram: **Settings › Devices** lists it as
*PhoneBox*.

## 3. Vibration and sound

| Setting | Meaning |
|---|---|
| Show the message text | Off: only the chat name (and nothing about the content) reaches the phone |
| Vibration | Off, short, double, long, triple or heartbeat |
| Sound | **As in Telegram** follows each chat's sound; or always no sound, or one of the phone's tones |
| Sound volume | Percent of the phone's media volume |
| Try on the phone | Shows a test notification with these settings |

**Chats with their own vibration or sound**: add a chat by its name as Telegram shows it, its `@username` or its id,
and pick its vibration and sound (**Default** uses the settings above). **Mute here** keeps a chat off the phone
without muting it in Telegram; **Even when muted** lets a chat through while it is muted in Telegram. The names of
chats that recently sent a message are listed under the button so you can copy them.

Like the other notifications, Telegram ones are silent while the night screen is on.

## Troubleshooting

| Problem | What to try |
|---|---|
| "Telegram refused the api_id / api_hash" | Copy both again from my.telegram.org, save, then **Sign in** |
| No code arrives | Look in Telegram on your other devices (the message comes from *Telegram*). Wait a minute before asking again: Telegram limits how often a code can be sent |
| "Telegram ended the saved login" | The session was ended from another device. **Sign in** again |
| A ringtone plays as a plain tone | Only ringtones saved to your Telegram account (Settings › Notifications › Sounds) can be fetched; a sound built into the Android or iOS app plays as the default tone |
| A muted group still notifies | You were mentioned or replied to; that is how Telegram behaves too |

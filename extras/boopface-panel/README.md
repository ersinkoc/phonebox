# BoopFace panel page + DeepSeek balance

Two additions to PhoneBox, built and tested on Linux Mint 22.3 / X11 with a realme RMX2170
(Android 12) as the desk panel.

## boopface/

A panel page that is just an animated face — [BoopFace](https://github.com/CX-ArtLab/BoopFace) (MIT)
inside an `html` widget. The library is bundled to one IIFE and inlined, so the panel needs no
network and no CDN; `bundle.sh` regenerates it from the published npm package.

```bash
python3 boopface/build.py                                     # install with saved settings
python3 boopface/build.py --set skin=305 --set motion=reduce  # change a documented option
```

The settings sheet on the phone exposes the library's own options — `skin`, `color`, `auto-sleep`,
`wake-on`, `motion`, `press-squeeze`, `antenna-drag`, plus `setPointerFollow()` and
`setHeadRoundness()`. A sandboxed iframe has no storage, so a change made on the phone lasts until
the panel reloads; `--set` bakes it in permanently.

Note: BoopFace refuses `press-squeeze` for `pointerType === 'touch'` (it is a mouse gesture), so on a
touchscreen the gesture needs a synthetic mouse event. `build.py` sends one when the option is on.

## ai-quota/

DeepSeek has no usage/quota endpoint, only `GET /user/balance`, so the quota card is a pushed value:

```bash
DEEPSEEK_API_KEY=sk-... python3 ai-quota/deepseek-balance.py
```

The key is read from `$DEEPSEEK_API_KEY` first, then from `~/.dsh/.credentials.yaml`. It never
reaches the panel, the config or this repository — only the balance is pushed.

```bash
cp ai-quota/phonebox-deepseek.{service,timer} ~/.config/systemd/user/
systemctl --user daemon-reload && systemctl --user enable --now phonebox-deepseek.timer
```

## Browser artwork

This branch also fixes `agent/PhoneBox.Agent/Unix/MediaMonitor.cs`: it required an image file
extension for `file://` artwork, but Chromium publishes the media-session image as an extension-less
temp file (`/tmp/.org.chromium.Chromium.XXXXXX`), so Brave/Chrome thumbnails were always dropped.
The extension gate is gone and the existing magic-byte sniff decides.

## Security

No credentials are in this folder:

- The agent's `config.json` (which holds `lan.token`, `lan.webhookToken` and the pinned TLS key) is
  machine state, never committed.
- The DeepSeek key is read at runtime; there is no key literal in any script here.

## Credits

- [PhoneBox](https://github.com/ersinkoc/phonebox) — MIT, by Ersin Koç.
- [BoopFace](https://github.com/CX-ArtLab/BoopFace) — MIT, by CX ArtLab; `boopface.iife.js` is a
  minified bundle of that library.

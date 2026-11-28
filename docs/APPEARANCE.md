# Appearance

Appearance is optional. Existing settings without appearance overrides retain their original theme, spacing,
widget-specific padding and responsive corner radii.

## Desktop editor

Open **General → Appearance** for global defaults. Choose a surface preset, then adjust:

| Setting | Range | Scope |
|---|---|---|
| Font | Original / sans / rounded / condensed / serif / mono | Global, page, widget |
| Font size | 50–200% | Global, page, widget |
| Corner radius | 0–32 CSS pixels | Global, page, widget |
| Widget spacing | 0–24 CSS pixels | Global, page |
| Widget inner padding | 0–24 CSS pixels | Global, page, widget |
| Page edge padding | 0–32 CSS pixels | Global, page |
| Widget border | Inherit / on / off | Global, page, widget |
| Widget shadow | Inherit / on / off | Global, page, widget |

Use **Show live appearance preview** and choose a page to see unsaved settings in the real panel renderer.
Save applies changes to connected phones. The preview does not save the settings by itself.

In **Pages**, open **Page appearance** to override global defaults. Select a widget and open **Widget appearance**
in its inspector to override that page. Widget duplication and page export/import keep these settings; Smart Stack
children inherit their stack's appearance.

## Presets and inheritance

- **Original appearance** restores the original renderer at that scope, including responsive corners and each
  widget's original padding. It can restore one original page or widget underneath a customized global preset.
- **Rounded cards** uses 18px corners, 12px spacing/padding, borders and no shadow.
- **Flat surfaces** uses solid surfaces, 12px corners/spacing/padding, no border and no shadow. Radius and spacing
  remain independent controls: a flat surface can have rounded corners.
- **Content only** uses transparent surfaces, square corners, 12px spacing/padding and no border/shadow.
- **Seamless panel** uses square corners, zero grid spacing, 12px inner padding and borders.
- **Glass** uses translucent cards with a light rim, a top sheen and a soft shadow, 20px corners, 10px spacing and
  12px padding, over a colourful background (or your wallpaper). The background is drawn only behind a page that has
  glass widgets, so the other pages keep theirs. Shortcut keys, process rows and AI quota cards get the glass, not the
  widget around them. Note, Image and the Discord bar keep their own colours. It does not blur what is behind the
  cards, so it stays smooth on older phones. Glass draws its own rim and shadow: a border or shadow switched off by a
  parent's preset (Flat, Content only) does not carry over, while one set on the same scope or below still applies.

Choosing a preset replaces overrides at that scope. Individual adjustments then override its defaults. **Inherit**
on an individual control removes only that explicit adjustment, restoring the preset/parent value.
**Reset to inherited appearance** removes all appearance overrides at that scope.

Theme and accent remain under Display. Flat removes decorative surface gradients/blur/shadow defaults while
retaining meaningful content such as album art, signal-meter colors and circular dials. Explicit shadow and border
choices take precedence over a preset. Page edge padding always reserves the device safe area and page-navigation
space. Grid spacing uses CSS grid gap, keeping widget coordinates unchanged.

## Wallpaper

**Settings › Display › Wallpaper** puts a picture of your own (JPEG, PNG or WEBP, up to 4 MB) behind every page.
**Darken** (0–90 %, default 45) lays black over it and **Blur** (0–20 px) softens it, so the widgets stay readable
on a busy photo. The picture is saved as soon as you choose it; Darken and Blur are saved with the other settings.
The widgets' surfaces are translucent, so the wallpaper shows through them; **Content only** or **Flat** lets more of
it through. While a wallpaper is set, the album-art tint of the background is not drawn.

The picture is kept next to the settings as `wallpaper.<ext>`. The panel asks the agent for it (`/api/wallpaper`) with
its revision in the address: a browser keeps it until the picture changes, while the Android app loads it again each
time the panel reloads (over USB or Wi-Fi), so a picture about the size of the phone's screen is the better choice.
Blur is redrawn by the phone; on older phones keep it at 0. In `config.json` the look is `display.wallpaper`:
`{ "dim": 45, "blur": 0 }`.

## Configuration

The same optional object is supported at `display.appearance`, `pages[].appearance` and
`pages[].widgets[].appearance`. Grid spacing and page edge padding are ignored at widget scope.

```json
{
  "display": {
    "appearance": {
      "style": "flat",
      "font": "sans",
      "fontScale": 120,
      "radius": 18,
      "gap": 12,
      "padding": 10,
      "border": false,
      "shadow": false
    }
  }
}
```

Supported style values are `default`, `cards`, `flat`, `borderless`, `seamless` and `glass`. Supported font values are
`default`, `sans`, `rounded`, `condensed`, `serif` and `mono`, and `fontScale` is clamped between `50` and `200`.
Omitted/null properties inherit. Zero and false are explicit choices. Imported numeric values are bounded;
unknown style or font names are discarded, and appearance does not accept arbitrary CSS.

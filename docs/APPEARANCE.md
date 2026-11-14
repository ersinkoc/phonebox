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

Choosing a preset replaces overrides at that scope. Individual adjustments then override its defaults. **Inherit**
on an individual control removes only that explicit adjustment, restoring the preset/parent value.
**Reset to inherited appearance** removes all appearance overrides at that scope.

Theme and accent remain under Display. Flat removes decorative surface gradients/blur/shadow defaults while
retaining meaningful content such as album art, signal-meter colors and circular dials. Explicit shadow and border
choices take precedence over a preset. Page edge padding always reserves the device safe area and page-navigation
space. Grid spacing uses CSS grid gap, keeping widget coordinates unchanged.

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

Supported style values are `default`, `cards`, `flat`, `borderless` and `seamless`. Supported font values are
`default`, `sans`, `rounded`, `condensed`, `serif` and `mono`, and `fontScale` is clamped between `50` and `200`.
Omitted/null properties inherit. Zero and false are explicit choices. Imported numeric values are bounded;
unknown style or font names are discarded, and appearance does not accept arbitrary CSS.

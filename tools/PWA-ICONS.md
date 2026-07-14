# PWA icon assets

The BotNexus PWA icons are generated programmatically from a single source of
truth so they can be regenerated deterministically and never drift back into the
empty solid-fill placeholders that shipped originally (see issue #1967).

## Source

`tools/generate-pwa-icons.py` renders the BotNexus robot mark (a rounded
dark-navy `#16213e` tile with a light robot head, indigo `#4f46e5` accents and
cyan `#6ee7ff` eyes) using Pillow. It emits, into both the desktop and mobile
Blazor client `wwwroot` folders:

| File | Purpose |
|---|---|
| `icon-192.png` / `icon-512.png` | `purpose: "any"` full-bleed icons |
| `icon-192-maskable.png` / `icon-512-maskable.png` | `purpose: "maskable"` with ~10% safe-zone padding so Windows/Android don't clip the mark |

The browser-tab `favicon.svg` is a hand-authored inline vector of the same mark
(no emoji-font dependency, no un-encoded glyph).

## Regenerate

```shell
python -X utf8 tools/generate-pwa-icons.py
```

Requires Pillow (`pip install pillow`). The script overwrites the PNGs in place.
After regenerating, run the guard tests:

```shell
dotnet test tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests --filter "FullyQualifiedName~PwaIconAssetTests"
```

These assert every icon is real artwork (multi-colour, above a minimum byte
size), that the maskable variant differs from the `any` variant, and that
`favicon.svg` contains no mangled `??` glyph.

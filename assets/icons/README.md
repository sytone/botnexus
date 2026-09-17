# App icons

This directory contains a set of 44 original interface icons drawn on a rounded 24 × 24 grid. The SVG files are the source artwork for BotNexus's generated Blazor icon library.

## Delivered files

- `svg/` — 44 individual SVG source files
- `preview.png` — a contact sheet of the icon set
- `README.md` — this guide

This directory does not provide PNG exports or a React component package.

## Use a raw SVG

For plain HTML, reference a file from `svg/`:

```html
<img src="/icons/svg/home.svg" width="24" height="24" alt="Home">
```

Neutral utility icons use `currentColor` and inherit CSS colour when embedded inline. Identity and state icons use the semantic palettes recorded in their SVG source.

## Generate the Blazor library

From the repository root, run:

```shell
python scripts/generate-icons.py
```

The generator reads every file in `assets/icons/svg/` and writes `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Components/IconLibrary.g.cs`. Change the SVG source and rerun the generator instead of editing `IconLibrary.g.cs` by hand.

BotNexus renders entries from the generated library through the Blazor `Icon` component. Its `Name` parameter is the SVG file name without the `.svg` extension. For example:

```razor
<Icon Name="home" Size="20" Title="Home" />
```

Omit `Title` when an icon is decorative or sits beside its own visible label. The component then hides it from assistive technology.

## Verify the inventory

From the repository root, run the generator. Its success message reports the number of SVG files that it read and generated:

```shell
python scripts/generate-icons.py
```

The generated-library test `IconLibraryTests.AssetReadmeDescribesTheDeliveredDistribution` also compares this README's count and artifact claims with the copied SVG inventory.

## Design notes

- 24 × 24 viewBox with transparent backgrounds
- 2 px strokes with round caps and joins
- Designed for 16, 20, 24, and 32 px user-interface use
- Restrained semantic colour: blue for communication and actions, green for activity and completion, amber for tools, scheduling, and temporary state, red for destructive or blocking actions, and gradients for AI, extension, and creative identities
- Original artwork with no dependency on an external icon font or runtime package

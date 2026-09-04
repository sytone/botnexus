# App Icons

A production-ready set of 29 original, rounded 24 × 24 interface icons. SVG files are the masters; PNGs are transparent 96 × 96 exports (4×), suitable for high-density web displays.

## Contents

- `svg/` — individual scalable icons
- `png/` — matching 96 × 96 transparent exports
- `index.tsx` — typed React components
- `preview.png` — contact sheet

## Plain HTML

```html
<img src="/icons/svg/home.svg" width="24" height="24" alt="Home">
```

Neutral utility icons use `currentColor` and inherit CSS color when embedded inline. Identity and state icons use the approved semantic palettes.

## React / TypeScript

Copy `index.tsx` into your project (React 18+), then import components directly:

```tsx
import { HomeIcon, AssistantIcon, DeleteIcon } from './icons';

<HomeIcon size={20} className="nav-icon" title="Home" />
<AssistantIcon size={24} title="Assistant" />
<DeleteIcon size={20} title="Delete" />
```

All components accept standard `SVGProps<SVGSVGElement>`. Omit `title` for decorative icons; they will be hidden from assistive technology.

## Icon names

- `home` → `HomeIcon`
- `activity` → `ActivityIcon`
- `tools` → `ToolsIcon`
- `chat` → `ChatIcon`
- `assistant` → `AssistantIcon`
- `configuration` → `ConfigurationIcon`
- `skills` → `SkillsIcon`
- `agents` → `AgentsIcon`
- `cron-jobs` → `CronJobsIcon`
- `plugins` → `PluginsIcon`
- `guide` → `GuideIcon`
- `bot` → `BotIcon`
- `conversation` → `ConversationIcon`
- `workspace` → `WorkspaceIcon`
- `reports` → `ReportsIcon`
- `canvas` → `CanvasIcon`
- `todo` → `TodoIcon`
- `visibility` → `VisibilityIcon`
- `pin` → `PinIcon`
- `delete` → `DeleteIcon`
- `move` → `MoveIcon`
- `send` → `SendIcon`
- `attach` → `AttachIcon`
- `avoid` → `AvoidIcon`
- `stop` → `StopIcon`
- `pause` → `PauseIcon`
- `light-mode` → `LightModeIcon`
- `dark-mode` → `DarkModeIcon`
- `usage` → `UsageIcon`

## Design notes

- 24 × 24 viewBox; transparent backgrounds
- 2 px strokes with round caps and joins
- Designed for 16, 20, 24, and 32 px UI use
- Restrained semantic color: blue for communication/actions, green for activity/completion, amber for tools/scheduling/temporary state, red for destructive/blocking actions, and expressive gradients for AI/extension/creative identities
- Original artwork; no dependency on an external icon font or runtime package

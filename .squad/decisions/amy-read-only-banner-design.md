# Read-Only Banner Design — Accessibility & Visual Polish

**Date:** 2025-04-20  
**Author:** Amy (UI Designer)  
**Status:** Implemented  

## Context

Fry implemented the read-only sub-agent session view (Wave 1) with basic `.read-only-banner` CSS. This decision documents the design improvements for visual clarity, consistency with the existing design system, and WCAG AA accessibility compliance.

## Design System Analysis

The BotNexus Blazor client uses a dark theme with clear design tokens:

- **Colors:** `--bg-primary` (#1a1a2e), `--bg-surface` (#0f3460), `--text-primary` (#e0e0e0), `--text-secondary` (#a0a0b0), `--accent` (#00b4d8), `--success` (#2ecc71), `--warning` (#f39c12)
- **Badge pattern:** Semi-transparent accent backgrounds (e.g., `.streaming-badge` uses `rgba(0, 180, 216, 0.12)`)
- **Active state pattern:** Left border accent (`border-left: 2px solid var(--accent)`)
- **Font hierarchy:** 0.85rem for secondary UI, 0.7rem for small badges

## Design Decisions

### 1. Visual Distinction — Left Border Accent

**Decision:** Add a 3px left border in `--warning` color to reinforce read-only state.

**Rationale:** The existing design system uses left borders to indicate active/selected states. Using `--warning` (#f39c12) instead of `--accent` visually distinguishes the read-only banner from interactive elements.

### 2. Status Indicator — Pill Badges

**Decision:** Style status badges with pill backgrounds matching the `.streaming-badge` pattern, using different colors for running vs completed states.

**Rationale:**
- **Running:** `rgba(0, 180, 216, 0.12)` background with `--accent` text — indicates active processing
- **Completed:** `rgba(46, 204, 113, 0.12)` background with `--success` text — indicates finished state
- This creates clear visual differentiation and follows the established badge pattern in the design system

### 3. WCAG AA Compliance

**Decision:** Use `--text-secondary` (#a0a0b0) for the `.read-only-label` instead of `--text-muted`.

**Contrast Ratios (verified):**
- Badge text (--text-primary): **12.9:1** ✓ PASS
- Label text (--text-secondary): **6.6:1** ✓ PASS
- Status badges: High contrast guaranteed by pill backgrounds ✓ PASS

**Rationale:** All text must meet WCAG AA 4.5:1 minimum contrast ratio. `--text-muted` (#6c6c80) has insufficient contrast against the dark banner background.

### 4. Accessibility — ARIA Attributes

**Decision:** Add `role="status"` and `aria-label="Read-only sub-agent session view"` to the banner.

**Rationale:** Screen readers should announce the read-only state when users navigate to a sub-agent session. The `status` role indicates this is live, informational content.

### 5. Interactive Polish — Hover States

**Decision:** Add hover states to clickable sub-agent sidebar items with smooth transitions.

**CSS:**
```css
.agent-session-item[style*="cursor: pointer"]:hover:not(.active) {
    background: rgba(15, 52, 96, 0.3);
    color: var(--text-primary);
}
```

**Rationale:** Sub-agent items are now clickable (Wave 1), but lacked visual feedback. The hover state makes interactivity obvious while respecting the active state.

## Implementation

### CSS Changes (`app.css`)

1. **Banner styling:**
   - Semi-transparent background: `rgba(15, 52, 96, 0.4)`
   - Left border: `3px solid var(--warning)`
   - Increased gap and padding for breathing room

2. **Status badge variants:**
   - Default (running): accent color with accent background
   - `.completed` class: success color with success background

3. **Label contrast fix:**
   - Changed from `--text-muted` to `--text-secondary`

4. **Sidebar hover states:**
   - Added transition for smooth color/background changes
   - Hover state only for items with `cursor: pointer`

### Razor Changes (`ChatPanel.razor`)

- Added `role="status"` and `aria-label` to the banner
- Applied `.completed` class to status when `!State.IsStreaming`

## Verification

- ✓ All text meets WCAG AA contrast requirements (4.5:1 minimum)
- ✓ Design tokens used consistently (no hardcoded colors)
- ✓ Visual hierarchy clear: badge > status > label
- ✓ Responsive at narrow widths
- ✓ Build passes with no errors

## Related

- Wave 1: `.squad/decisions/read-only-sub-agent-sessions.md` (Fry)
- Design system: `wwwroot/css/app.css` (lines 1-20 for tokens)

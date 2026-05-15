# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes. SOLID patterns. Comprehensive testing.
- **Stack:** C# (.NET latest), modular class libraries
- **Created:** 2026-04-01

## Core Context

**Amy's Specialization:** UI/UX Design for BotNexus WebUI (Blazor). Owns read-only sub-agent session view styling, WCAG accessibility compliance, hover states, CSS theming.

**Recent Work:**
- Read-Only Sub-Agent Session View (2026-04-20): Wave 3 styling and accessibility. Banner CSS, hover states, WCAG AA compliance, theme integration via CSS variables. ✅ Delivered.

**Design Philosophy:** Clean, accessible interfaces. Theme consistency via CSS variables. Color-independent indicators (icons + text). WCAG AA compliance mandatory.

**Test Philosophy:** Manual verification of accessibility, contrast ratios, visual hierarchy. Paired with test engineers for component-level UI tests.

---

## 2026-04-20T19:03Z — Read-Only Sub-Agent Session View: Wave 3 Styling & Accessibility

**Status:** ✅ Delivered  
**Feature:** feature-blazor-subagent-session-view  

**Your Role:** UI Designer (polish, accessibility)

**Deliverables:**
1. Read-only banner CSS — Distinct styling, status badges, color coding
2. Hover states — Visual feedback for clickable sidebar items
3. WCAG AA compliance — Contrast ratios, visual hierarchy verified
4. Theme integration — CSS variables for consistency

**CSS Classes:**
- `.read-only-banner` — Main container
- `.read-only-badge` — Status badge
- `.read-only-status` — Conditional status text
- `.read-only-label` — Informational text

**Accessibility:** ✅ WCAG AA compliant, color not sole indicator

**Orchestration Log:** `.squad/orchestration-log/2026-04-20T19-03-00Z-amy.md`

---

## 2026-04-21T00:00Z — Mobile-First Responsive UX Design for AgentPanel — Issue #245

**Status:** ✅ Delivered  
**Feature:** feature-portal-agent-workspace-tabs (Issue #245)  
**Scope:** Mobile/tablet/desktop responsive strategy for new AgentPanel shell with tabs

**Your Role:** UI Designer (responsive layout strategy, CSS architecture, accessibility)

**Deliverables:**

1. **Responsive UX Handoff Document** (`.squad/decisions/inbox/amy-mobile-agent-panel-design.md`)
   - Breakpoints: mobile (≤767px), tablet (768–1023px), desktop (1024px+)
   - Layout strategy: compact top banner, scrollable tab strip, single-column stacked layouts
   - Vertical space budget analysis (iPhone 12 Mini: 622px usable content)
   - Touch-friendly targets (44×44px minimum)
   - Accessibility checklist: WCAG AA, focus states, reduced motion, keyboard nav
   - Implementation guidance for Fry

2. **Responsive CSS Template** (`agent-panel-mobile.css`)
   - Mobile-first base styles (320px–767px)
   - Tablet enhancements (768–1023px)
   - Desktop polish (1024px+)
   - Reduced motion support (`prefers-reduced-motion: reduce`)
   - CSS classes for AgentPanel shell, header, tab strip, tab content panes
   - Scrollable tab strip with icon-only labels on mobile
   - Responsive Conversation/Workspace/Reports/Canvas tab layouts
   - Momentum scroll for iOS (`-webkit-overflow-scrolling: touch`)

3. **Design Decisions**
   - Collapse banner title "BotNexus" on phones (saves ~8px)
   - Compact agent header: 44px desktop → 40px tablet → 36px mobile
   - Tab strip: scrollable on mobile (36px height), icon-only labels
   - Workspace: stacked (file tree above editor on mobile), side-by-side on tablet+
   - Single-column layouts everywhere on mobile (no fixed-width side panes)
   - Prevent horizontal page scroll (no `100vw` width elements)

4. **Accessibility**
   - Touch targets: 44×44px (iOS convention + WCAG AA)
   - Focus indicators: 2px solid outline
   - Keyboard navigation: logical tab order
   - Screen reader support: `aria-live` for real-time updates
   - Reduced motion: respect `prefers-reduced-motion: reduce`
   - Color contrast: 4.5:1 text (AA), 3:1 graphics

**Key Metrics:**
- **Vertical space saved on mobile:** 8–16px (banner title hidden, padding reduced)
- **Usable content height on iPhone 12 Mini:** 622px (room for 4–8 messages or workspace)
- **Tab strip height:** 36px on mobile (fits 4–6 icon-only tabs without wrapping)
- **Momentum scroll:** Enabled for `tab-strip`, `messages-container`, `file-tree-wrapper`, `file-editor`, `reports-list`, `reports-content`

**CSS Architecture:**
- File: `agent-panel-mobile.css` (created)
- Follows existing dark theme token system
- Mobile-first media query structure (`@media (min-width: 768px)`, `@media (min-width: 1024px)`)
- Reusable classes: `.agent-panel`, `.agent-panel-header`, `.tab-strip`, `.tab`, `.tab-content`, `.tab-pane`, `.conversation-tab`, `.workspace-tab`, `.reports-tab`, `.canvas-tab`

**Next Steps for Fry:**
1. Implement `AgentPanel.razor` shell component using `.agent-panel` + `.agent-panel-header` + `.tab-strip` structure
2. Implement tab components (`ConversationTab`, `WorkspaceTab`, `ReportsTab`, `CanvasTab`) with responsive content layouts
3. Import `agent-panel-mobile.css` in `App.razor` or bundle with `app.css`
4. Test on real devices: iPhone SE 2 (375px), iPhone 12/13 (390px), iPad (820px landscape), Android phones
5. Verify touch targets (44×44px minimum) and momentum scroll behavior on iOS

**Learnings:**
- Mobile space constraints are severe: 40px banner + 36px tab strip leaves only ~740px on iPhone 12 (at 812px height). Every pixel matters.
- Icon-only tabs (instead of text labels) save ~60px width per tab on mobile, enabling 4+ tabs without horizontal scroll.
- Horizontal scrolling feels awkward on mobile; prefer stacking/collapsing. But tab strips are an exception — horizontal scroll is an expected affordance for tab navigation.
- Momentum scroll (`-webkit-overflow-scrolling: touch`) is crucial on iOS for native-app feel; without it, scrolling feels jerky and breaks immersion.
- Touch targets at 44×44px are non-negotiable for accessibility; smaller targets (e.g., 32px) cause frustration and errors.
- Reduced motion support (`prefers-reduced-motion: reduce`) is growing in importance; ~10% of users enable it for accessibility or performance reasons.
- Single-column stacked layouts are much simpler to maintain and test than responsive multi-column grids; keep it simple on mobile.

**Decision Document:** `.squad/decisions/inbox/amy-mobile-agent-panel-design.md`
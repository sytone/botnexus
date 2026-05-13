# Fry Decision: Locations Config UI

**Author:** Fry (Web Dev)
**Date:** 2026-07-29
**Scope:** BlazorClient UI — Locations management
**Status:** Implemented

## Context

The configuration UI had no locations section. Users needed to edit `config.json` manually to add, update, or remove named locations. Bender had already built a full REST API at `/api/locations` (CRUD + health check) in `LocationsController`.

## Decision

Created a standalone `LocationsConfigPanel` component that calls the locations REST API directly (via a new `LocationsApiClient` service) rather than going through the generic `PlatformConfigService` JSON section approach.

### Why a dedicated API client?

The locations REST API provides domain-aware validation, health checks, and proper per-entry CRUD — features that the generic config section save (`PUT /api/config/gateway`) cannot provide. Using the dedicated API gives users real-time feedback and prevents invalid entries.

### Backend contract assumed

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/locations` | List all locations |
| POST | `/api/locations` | Create a location |
| GET | `/api/locations/{name}` | Get single location |
| PUT | `/api/locations/{name}` | Update a location |
| DELETE | `/api/locations/{name}` | Delete a location |
| POST | `/api/locations/{name}/check` | Health check |

All endpoints are implemented in `LocationsController` (Bender).

### Files added/modified

- **Added:** `Services/LocationsApiClient.cs` — typed HTTP client with DTOs
- **Added:** `Components/Config/LocationsConfigPanel.razor` — full CRUD + health check UI
- **Added:** `LocationsConfigPanelTests.cs` — 16 bUnit component tests
- **Added:** `LocationsApiClientTests.cs` — 10 service-level tests
- **Modified:** `Program.cs` — DI registration for `LocationsApiClient`
- **Modified:** `Pages/Configuration.razor` — `case "locations"` routing (was already present; updated to use standalone panel)

### UX details

- System-managed locations (e.g., agents-dir, sessions) are shown read-only with a "system" badge
- User-defined locations have edit (✏️) and delete (🗑️) buttons
- Health check button (🩺) updates status inline
- Form validates name and value before submitting
- API errors surface in a dismissible banner without crashing
- Changes take effect immediately via the REST API — no restart needed

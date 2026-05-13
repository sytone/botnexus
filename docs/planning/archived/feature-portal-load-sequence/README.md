# Portal Load Sequence Redesign

## Required deliverables

The design review MUST include Mermaid sequence diagrams for:

1. **Site startup sequence** — from page load to IsReady=true
2. **Agent+conversation select** — from sidebar click to history rendered
3. **Outbound message flow** — from user send to response rendered
4. **Inbound event flow** — from SignalR event to UI update
5. **External conversation** — new conversation arrives from Telegram/other channel

## Diagram format

Use Mermaid sequenceDiagram blocks in the markdown:

```
sequenceDiagram
    participant Browser
    participant MainLayout
    participant PortalLoadService
    participant GatewayRestClient
    participant SignalR
    participant Gateway

    Browser->>MainLayout: OnInitializedAsync
    MainLayout->>PortalLoadService: InitializeAsync()
    ...
```

## Why diagrams

- Unambiguous spec for implementors
- Reference for code review
- Catches design problems before code is written
- Future maintainers understand the system without reading 1300 lines of code

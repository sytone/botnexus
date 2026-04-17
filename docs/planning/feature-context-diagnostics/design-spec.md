---
id: feature-context-diagnostics
title: "Context Diagnostics — /context command + debug API"
type: feature
priority: critical
status: in-progress
created: 2026-04-17
tags: [diagnostics, context, debugging, tools, system-prompt]
---

# Feature: Context Diagnostics

**Status:** draft  
**Priority:** critical

## Problem

No way to see what's being sent to the LLM — system prompt, tool definitions, history, total context size. Debugging tool failures requires guesswork.

## Commands

| Command | Description |
|---------|-------------|
| `/context` | Context summary: total tokens, system prompt %, history %, tools % |
| `/context export` | Export full context JSON to logs for investigation |
| `/context summary` | Breakdown by source: files, prompt sections, tool count, history |

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /api/agents/{id}/sessions/{sid}/context` | Context summary |
| `GET /api/agents/{id}/sessions/{sid}/context/system-prompt` | Full system prompt |
| `GET /api/agents/{id}/sessions/{sid}/context/tools` | Tool definitions with schemas |
| `POST /api/agents/{id}/sessions/{sid}/context/export` | Export to logs |

## Token Estimation

Current: fixed `chars/4`. Should be model-aware (Claude ~3.5, GPT ~4.0).

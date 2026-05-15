---
name: sample-advanced-status-report
description: Advanced Markdown template showing defaults, required fields, and multi-line structure.
defaults:
  report_type: weekly
  date: today
parameters:
  project:
    description: Name of the project or service
    required: true
  owner:
    description: Team lead or owner email
    default: team@example.com
  status:
    description: Current status such as on-track, at-risk, or blocked
    default: on-track
  summary:
    description: Brief summary of progress and blockers
    required: true
---
# {{report_type}} Status Report

## Project Details

- Project: {{project}}
- Owner: {{owner}}
- Date: {{date}}
- Status: {{status}}

## Summary

{{summary}}

## Suggested Sections

1. Accomplishments since last update
2. Current blockers or risks
3. Next actions

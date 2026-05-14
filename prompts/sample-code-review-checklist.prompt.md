---
name: sample-code-review-checklist
description: Structured code review template with multiple sections
parameters:
  prNumber:
    description: Pull request number or identifier
    required: true
  repo:
    description: Repository name
    default: botnexus
    required: false
  reviewer:
    description: Name of the code reviewer
    required: true
  focusArea:
    description: "Primary focus area: architecture, performance, tests, or security"
    default: overall quality
    required: false
---
# Code Review Checklist for PR #{{prNumber}}

**Repository:** {{repo}}  
**Reviewer:** {{reviewer}}  
**Focus Area:** {{focusArea}}

---

## Code Quality & Style

- [ ] Code follows the project style guide
- [ ] Variable and function names are clear and descriptive
- [ ] Comments explain the "why", not the "what"
- [ ] No dead code or commented-out sections

## Logic & Functionality

- [ ] Changes implement the intended feature or fix
- [ ] Logic is clear and easy to follow
- [ ] Edge cases are handled properly
- [ ] Error handling is appropriate

## Testing

- [ ] Unit tests added or updated
- [ ] Test coverage maintained or improved
- [ ] Tests are meaningful and not brittle
- [ ] Integration tests pass

## Performance & Security

- [ ] No obvious performance regressions
- [ ] No security vulnerabilities introduced
- [ ] SQL queries are optimized
- [ ] External APIs called efficiently

## Documentation

- [ ] Changes documented (code comments, README, docs)
- [ ] API signatures documented if applicable
- [ ] Breaking changes clearly noted

---

## Summary

**Approved:** ✓ or ✗  
**Comments:** Provide constructive feedback and any requested changes.

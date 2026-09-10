#!/usr/bin/env bash
# Install (or refresh) the get-to-know-you skill.
#
# Unlike the guide skill this has no reference files - it is one SKILL.md and nothing
# else, because the thing it teaches is a conversation, not a body of documentation.
# It still gets an install script rather than an MSBuild target for the same reason:
# skills live under ~/.botnexus, which is user data, and an operator decides whether an
# agent has this one.
#
# Usage: scripts/install-interview-skill.sh [--home <dir>] [--agent <agent-id>]
#
# With --agent the skill is installed for that agent only. Worth considering: an agent
# that interviews you is right for one you work with directly and wrong for a cron
# worker that has no user to ask.
set -euo pipefail

BOTNEXUS_HOME="${BOTNEXUS_HOME:-$HOME/.botnexus}"
AGENT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --home)  BOTNEXUS_HOME="${2:?--home needs a directory}"; shift 2 ;;
        --agent) AGENT="${2:?--agent needs an agent id}"; shift 2 ;;
        *)       echo "error: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKILL_SRC="$REPO_ROOT/docs/interview-skill/SKILL.md"

if [ ! -f "$SKILL_SRC" ]; then
    echo "error: missing $SKILL_SRC — run this from a checkout of the repository" >&2
    exit 1
fi

if [ -n "$AGENT" ]; then
    DEST="$BOTNEXUS_HOME/agents/$AGENT/skills/get-to-know-you"
else
    DEST="$BOTNEXUS_HOME/skills/get-to-know-you"
fi

mkdir -p "$DEST"
cp "$SKILL_SRC" "$DEST/SKILL.md"

echo "installed get-to-know-you -> $DEST"
if [ -z "$AGENT" ]; then
    echo
    echo "The skill is global, so every agent can load it - including unattended ones with"
    echo "nobody to interview. To scope it to a single agent instead, re-run with:"
    echo "  scripts/install-interview-skill.sh --agent <agent-id>"
fi

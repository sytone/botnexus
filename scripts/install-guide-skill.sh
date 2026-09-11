#!/usr/bin/env bash
# Install (or refresh) the botnexus-guide skill.
#
# The skill's reference files are copies of docs/user-guide, so an agent can read the
# same material the portal's Guide page renders. Skills live under ~/.botnexus, which is
# user data rather than build output, so this cannot be an MSBuild target — it is a
# deliberate install step, re-run after upgrading to pick up documentation changes.
#
# Usage: scripts/install-guide-skill.sh [--home <dir>]
set -euo pipefail

BOTNEXUS_HOME="${BOTNEXUS_HOME:-$HOME/.botnexus}"
if [ "${1:-}" = "--home" ] && [ -n "${2:-}" ]; then
    BOTNEXUS_HOME="$2"
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DOCS="$REPO_ROOT/docs/user-guide"
SRC_SKILL="$REPO_ROOT/docs/skills.md"
SKILL_SRC="$REPO_ROOT/docs/guide-skill/SKILL.md"
DEST="$BOTNEXUS_HOME/skills/botnexus-guide"

for required in "$SRC_DOCS" "$SKILL_SRC"; do
    if [ ! -e "$required" ]; then
        echo "error: missing $required — run this from a checkout of the repository" >&2
        exit 1
    fi
done

mkdir -p "$DEST/reference"

# --delete so a page removed upstream does not linger and get quoted back at a user as
# though it were current. Confined to reference/ — never the skill root, which holds
# SKILL.md and anything an operator added by hand.
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete "$SRC_DOCS/" "$DEST/reference/"
else
    rm -rf "${DEST:?}/reference"
    mkdir -p "$DEST/reference"
    cp -R "$SRC_DOCS/." "$DEST/reference/"
fi

cp "$SRC_SKILL" "$DEST/reference/skills.md"
cp "$SKILL_SRC" "$DEST/SKILL.md"

# guide-index.json orders the portal's navigation and means nothing to an agent reading files.
rm -f "$DEST/reference/guide-index.json"

echo "installed botnexus-guide -> $DEST"
echo "  reference pages: $(find "$DEST/reference" -name '*.md' | wc -l | tr -d ' ')"
echo
echo "The skill is global, so every agent can load it. To scope it to one agent instead,"
echo "move it to $BOTNEXUS_HOME/agents/<agent-id>/skills/botnexus-guide."

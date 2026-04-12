#!/usr/bin/env python3
"""BotNexus session debug tool — look up sessions from SQLite or file stores."""

import argparse
import json
import os
import sqlite3
import sys
from pathlib import Path


def resolve_profile() -> Path:
    """Resolve the .botnexus profile directory."""
    custom = os.environ.get("BOTNEXUS_HOME")
    if custom:
        return Path(custom)
    return Path.home() / ".botnexus"


def resolve_db(profile: Path) -> Path | None:
    """Find the SQLite database from config or default location."""
    config_path = profile / "config.json"
    if config_path.exists():
        try:
            config = json.loads(config_path.read_text(encoding="utf-8"))
            store = config.get("gateway", {}).get("sessionStore", {})
            if store.get("type", "").lower() == "sqlite":
                conn_str = store.get("connectionString", "")
                if "Data Source=" in conn_str:
                    db_path = conn_str.split("Data Source=", 1)[1].split(";")[0].strip()
                    return Path(db_path)
        except (json.JSONDecodeError, KeyError):
            pass

    default = profile / "sessions.db"
    if default.exists():
        return default
    return None


def format_row(row: dict, columns: list[str]) -> str:
    """Format a row for display."""
    parts = []
    for col in columns:
        val = row.get(col, "")
        if val is None:
            val = "<null>"
        parts.append(f"{col}={val}")
    return " | ".join(parts)


def lookup_by_id(conn: sqlite3.Connection, partial_id: str, show_history: bool):
    """Find session(s) matching a partial ID."""
    cur = conn.cursor()
    cur.execute(
        "SELECT id, agent_id, channel_type, status, session_type, created_at, updated_at "
        "FROM sessions WHERE id LIKE ? ORDER BY updated_at DESC",
        (f"%{partial_id}%",),
    )
    rows = cur.fetchall()
    cols = ["id", "agent_id", "channel_type", "status", "session_type", "created_at", "updated_at"]

    if not rows:
        print(f"No sessions found matching '{partial_id}'")
        return

    print(f"Found {len(rows)} session(s):\n")
    for row in rows:
        d = dict(zip(cols, row))
        print(f"  ID:      {d['id']}")
        print(f"  Agent:   {d['agent_id']}")
        print(f"  Channel: {d['channel_type']}")
        print(f"  Status:  {d['status']}")
        print(f"  Type:    {d['session_type']}")
        print(f"  Created: {d['created_at']}")
        print(f"  Updated: {d['updated_at']}")

        # History count
        cur.execute(
            "SELECT COUNT(*), "
            "SUM(CASE WHEN is_compaction_summary = 1 THEN 1 ELSE 0 END) "
            "FROM session_history WHERE session_id = ?",
            (d["id"],),
        )
        count, summaries = cur.fetchone()
        print(f"  History: {count} entries ({summaries or 0} compaction summaries)")

        if show_history and count > 0:
            cur.execute(
                "SELECT role, substr(content, 1, 120), timestamp "
                "FROM session_history WHERE session_id = ? ORDER BY id DESC LIMIT 20",
                (d["id"],),
            )
            print(f"\n  Last 20 messages (newest first):")
            for h in cur.fetchall():
                content_preview = (h[1] or "").replace("\n", " ")
                print(f"    [{h[0]:9s}] {h[2]} | {content_preview}...")

        print()


def list_agent_sessions(conn: sqlite3.Connection, agent_id: str, visible_only: bool):
    """List sessions for a specific agent."""
    cur = conn.cursor()
    query = (
        "SELECT s.id, s.channel_type, s.status, s.session_type, s.updated_at, "
        "(SELECT COUNT(*) FROM session_history h WHERE h.session_id = s.id) as msg_count "
        "FROM sessions s WHERE s.agent_id = ? "
    )
    if visible_only:
        query += "AND s.session_type = 'user-agent' AND s.status IN ('Active', 'Suspended', 'Sealed') "
    query += "ORDER BY s.updated_at DESC"

    cur.execute(query, (agent_id,))
    rows = cur.fetchall()

    if not rows:
        print(f"No sessions found for agent '{agent_id}'")
        return

    print(f"Sessions for '{agent_id}' ({'visible only' if visible_only else 'all'}): {len(rows)}\n")
    print(f"  {'ID':<36s} {'Channel':<10s} {'Status':<10s} {'Type':<16s} {'Msgs':>5s} {'Updated'}")
    print(f"  {'─'*36} {'─'*10} {'─'*10} {'─'*16} {'─'*5} {'─'*26}")
    for row in rows:
        sid = row[0][:36] if len(row[0]) > 36 else row[0]
        print(f"  {sid:<36s} {(row[1] or '-'):<10s} {(row[2] or '-'):<10s} {(row[3] or 'None'):<16s} {row[4] or 0:>5d} {row[5]}")


def list_recent(conn: sqlite3.Connection, count: int):
    """List the most recent sessions across all agents."""
    cur = conn.cursor()
    cur.execute(
        "SELECT s.id, s.agent_id, s.channel_type, s.status, s.session_type, s.updated_at, "
        "(SELECT COUNT(*) FROM session_history h WHERE h.session_id = s.id) as msg_count "
        "FROM sessions s ORDER BY s.updated_at DESC LIMIT ?",
        (count,),
    )
    rows = cur.fetchall()

    if not rows:
        print("No sessions found")
        return

    print(f"Most recent {len(rows)} sessions:\n")
    print(f"  {'ID':<36s} {'Agent':<12s} {'Channel':<10s} {'Status':<10s} {'Type':<16s} {'Msgs':>5s} {'Updated'}")
    print(f"  {'─'*36} {'─'*12} {'─'*10} {'─'*10} {'─'*16} {'─'*5} {'─'*26}")
    for row in rows:
        sid = row[0][:36] if len(row[0]) > 36 else row[0]
        print(f"  {sid:<36s} {(row[1] or '-'):<12s} {(row[2] or '-'):<10s} {(row[3] or '-'):<10s} {(row[4] or 'None'):<16s} {row[5] or 0:>5d} {row[6]}")


def main():
    parser = argparse.ArgumentParser(description="BotNexus session debug tool")
    parser.add_argument("session_id", nargs="?", help="Partial session ID to look up")
    parser.add_argument("--agent", "-a", help="List sessions for a specific agent")
    parser.add_argument("--recent", "-r", type=int, help="Show N most recent sessions")
    parser.add_argument("--history", action="store_true", help="Show message history for matched sessions")
    parser.add_argument("--visible", action="store_true", help="Only show sessions visible in WebUI")
    parser.add_argument("--profile", help="Override .botnexus profile path")

    args = parser.parse_args()
    profile = Path(args.profile) if args.profile else resolve_profile()
    db_path = resolve_db(profile)

    if not db_path or not db_path.exists():
        print(f"SQLite database not found. Checked: {db_path or profile / 'sessions.db'}")
        print(f"Profile dir: {profile}")
        print("Is sessionStore.type set to 'Sqlite' in config.json?")
        sys.exit(1)

    conn = sqlite3.connect(str(db_path))
    try:
        if args.session_id:
            lookup_by_id(conn, args.session_id, args.history)
        elif args.agent:
            list_agent_sessions(conn, args.agent, args.visible)
        elif args.recent:
            list_recent(conn, args.recent)
        else:
            list_recent(conn, 10)
    finally:
        conn.close()


if __name__ == "__main__":
    main()

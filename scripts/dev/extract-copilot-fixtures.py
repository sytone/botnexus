"""Extract per-response SSE/JSON fixtures from Copilot mitmproxy captures.

Reads every flows-<model>.mitm in INPUT_DIR and writes per-response fixtures
to OUTPUT_DIR with this structure:

    <api>/<model>/req-<index>.body.txt    -- the raw response body
    <api>/<model>/req-<index>.meta.json   -- request/response metadata

The `<api>` segment is one of:
    messages       (POST /v1/messages)
    responses      (POST /responses)
    completions    (POST /chat/completions)

This script is the regeneration recipe for fixtures consumed by the
CopilotWireSnapshotTests harness. Run after collecting fresh captures with
scripts/dev/capture-copilot-flows.ps1.

USAGE:
    python scripts/dev/extract-copilot-fixtures.py \
        --input  $HOME/captures \
        --output tests/agent/BotNexus.Agent.Providers.Copilot.Tests/Fixtures/Wire \
        --models claude-haiku-4.5,gpt-5.5,mai-code-1-flash-internal,gemini-3.5-flash

If --models is omitted, every flows-*.mitm in --input is processed.
"""
import argparse
import json
import re
import sys
from pathlib import Path

try:
    from mitmproxy import io as mitm_io
except ImportError:
    print("ERROR: mitmproxy is not installed. Install with: pip install mitmproxy", file=sys.stderr)
    sys.exit(2)


API_BY_PATH = {
    "/v1/messages":      "messages",
    "/responses":        "responses",
    "/chat/completions": "completions",
}


def is_capture_target(req) -> str | None:
    """Return the API class string if req hits one of the target endpoints."""
    for path_prefix, api in API_BY_PATH.items():
        if req.path.startswith(path_prefix) and req.method == "POST":
            return api
    return None


def slugify(s: str) -> str:
    return re.sub(r"[^a-zA-Z0-9._-]", "_", s)


# Matches "encrypted_content":"<...>" or "encrypted_reasoning":"<...>" inside
# JSON SSE payloads. We replace the value with a short marker so the parser
# sees a non-empty string but the fixture stays diff-friendly.
ENCRYPTED_FIELD_RE = re.compile(
    r'"(encrypted_content|encrypted_reasoning)"\s*:\s*"[^"\\]*(?:\\.[^"\\]*)*"'
)

# Matches any JSON string value (in a key:"value" pair) longer than 200 chars.
# /responses bodies are dominated by 1000+ char opaque ids (response.id,
# item.id, content_part.id, etc.) — truncating them shrinks fixtures by ~95%
# while preserving every structural element the parser cares about.
LONG_STRING_VALUE_RE = re.compile(
    r'("[a-zA-Z0-9_]+"\s*:\s*")([^"\\]{200,}(?:\\.[^"\\]*)*)(")'
)


def redact_encrypted(body: str) -> str:
    """Shrink /responses fixtures from ~1.5MB to ~30KB:
    - Replace encrypted_content / encrypted_reasoning values with "<redacted>".
    - Truncate any other string value longer than 200 chars (giant opaque ids).

    Keeps the JSON structure intact so the parser still tokenises every event
    in the original order; only the unimportant blobs are shortened."""
    body = ENCRYPTED_FIELD_RE.sub(r'"\1":"<redacted>"', body)
    body = LONG_STRING_VALUE_RE.sub(r'\1<truncated>\3', body)
    return body


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--input",  required=True, help="Folder containing flows-<model>.mitm files")
    ap.add_argument("--output", required=True, help="Folder to write fixtures into")
    ap.add_argument("--models", default="", help="Comma-separated model ids to extract (default: all)")
    ap.add_argument("--max-per-model", type=int, default=0,
                    help="Cap on fixtures emitted per model (0 = no cap). Useful for keeping repo size small.")
    ap.add_argument("--redact-encrypted", action="store_true",
                    help="Replace encrypted_content/encrypted_reasoning fields with short placeholders. "
                         "Strongly recommended for /responses fixtures (shrinks 1MB blobs to ~5KB).")
    args = ap.parse_args()

    input_dir  = Path(args.input)
    output_dir = Path(args.output)
    selected   = set(filter(None, args.models.split(",")))

    if not input_dir.is_dir():
        print(f"ERROR: input dir not found: {input_dir}", file=sys.stderr)
        return 2

    captures = sorted(input_dir.glob("flows-*.mitm"))
    if not captures:
        print(f"ERROR: no flows-*.mitm files in {input_dir}", file=sys.stderr)
        return 2

    total = 0
    for capture in captures:
        model = capture.stem.replace("flows-", "", 1)
        if selected and model not in selected:
            continue

        with capture.open("rb") as fh:
            try:
                flows = list(mitm_io.FlowReader(fh).stream())
            except Exception as e:
                print(f"  ! parse error in {capture.name}: {e}", file=sys.stderr)
                continue

        seq = 0
        emitted_for_model = 0
        for flow in flows:
            req = getattr(flow, "request", None)
            resp = getattr(flow, "response", None)
            if not req or not resp:
                continue

            api = is_capture_target(req)
            if not api:
                continue

            seq += 1
            if args.max_per_model and emitted_for_model >= args.max_per_model:
                continue

            target_dir = output_dir / api / slugify(model)
            target_dir.mkdir(parents=True, exist_ok=True)

            base = f"req-{seq:02d}"
            body_path = target_dir / f"{base}.body.txt"
            meta_path = target_dir / f"{base}.meta.json"

            try:
                resp_body = resp.get_text(strict=False) or ""
            except Exception:
                resp_body = (
                    resp.raw_content.decode("utf-8", errors="replace")
                    if resp.raw_content else ""
                )

            if args.redact_encrypted:
                resp_body = redact_encrypted(resp_body)

            body_path.write_text(resp_body, encoding="utf-8", newline="\n")

            req_body = ""
            if req.raw_content:
                try:
                    req_body = req.get_text(strict=False) or ""
                except Exception:
                    req_body = req.raw_content.decode("utf-8", errors="replace")

            meta = {
                "model": model,
                "api": api,
                "request": {
                    "method": req.method,
                    "path":   req.path,
                    "host":   req.host,
                    "content_type": req.headers.get("content-type", ""),
                    "body_length": len(req_body.encode("utf-8")),
                },
                "response": {
                    "status_code": resp.status_code,
                    "content_type": resp.headers.get("content-type", ""),
                    "byte_length": len(resp_body.encode("utf-8")),
                },
            }
            meta_path.write_text(
                json.dumps(meta, indent=2, sort_keys=True),
                encoding="utf-8", newline="\n",
            )
            total += 1
            emitted_for_model += 1

    print(f"Wrote {total} fixtures into {output_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

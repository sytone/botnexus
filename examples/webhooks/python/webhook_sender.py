#!/usr/bin/env python3
"""BotNexus webhook sender example - all response modes.

Demonstrates how to talk to the BotNexus webhook API from Python:

  1. Register an inbound webhook via ``POST api/webhooks/registrations`` and
     capture the one-time ``secret``.
  2. Sign each inbound delivery with HMAC-SHA256 using the stdlib ``hmac`` and
     ``hashlib`` modules, matching the ``X-BotNexus-Signature-256`` header the
     gateway verifies (same convention as GitHub/Stripe).
  3. Send an inbound message in every response mode:
       * ``async``  - 202 + poll ``GET api/webhooks/runs/{runId}`` for the result.
       * ``sync``   - 200 with the agent response inline.
       * ``callback`` - 202; the gateway POSTs the result to your ``callbackUrl``.

Two transport variants are shown:
  * an **async** variant built on ``httpx.AsyncClient``.
  * a **sync** variant built on ``requests``.

Both variants are exercised by ``main()`` so the script is runnable directly:

    export BOTNEXUS_WEBHOOK_SECRET=whsec_...      # only if reusing a secret
    python webhook_sender.py

Requires Python 3.9+.
"""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import os
import sys
import time
from typing import Any, Dict, Literal, Optional, Tuple

# Response modes accepted by the gateway. ``None`` means "use the registration
# default", which is ``async``.
ResponseMode = Literal["async", "sync", "callback"]

# ---------------------------------------------------------------------------
# Configuration (override via environment variables).
# ---------------------------------------------------------------------------
BASE_URL: str = os.environ.get("BOTNEXUS_BASE_URL", "http://localhost:5000").rstrip("/")
AGENT_ID: str = os.environ.get("BOTNEXUS_AGENT_ID", "farnsworth")
API_TOKEN: Optional[str] = os.environ.get("BOTNEXUS_API_TOKEN")

# The inbound signing secret. Read from the environment when reusing a secret
# from a previous registration; otherwise a fresh registration supplies one.
SECRET_ENV_VAR: str = "BOTNEXUS_WEBHOOK_SECRET"

# Signature header the gateway expects (HMAC-SHA256 of the raw request body).
SIGNATURE_HEADER: str = "X-BotNexus-Signature-256"


# ---------------------------------------------------------------------------
# HMAC signing - stdlib only.
# ---------------------------------------------------------------------------
def compute_signature(secret: str, body: bytes) -> str:
    """Return the ``X-BotNexus-Signature-256`` value for ``body``.

    The gateway computes ``HMAC-SHA256(secret_utf8, raw_body_bytes)`` and
    compares it (constant-time) against the header value, which is formatted as
    ``sha256=<lowercase hex>``. You MUST sign the exact bytes you send on the
    wire, so callers should serialise the JSON body once and reuse those bytes
    for both signing and sending.
    """
    digest: str = hmac.new(
        secret.encode("utf-8"),
        body,
        hashlib.sha256,
    ).hexdigest()
    return f"sha256={digest}"


def encode_body(payload: Dict[str, Any]) -> bytes:
    """Serialise a JSON payload to the exact bytes that will be signed and sent.

    ``separators`` pins a stable, compact encoding so the signed bytes and the
    transmitted bytes are identical - a mismatch produces a 401 from the gateway.
    """
    return json.dumps(payload, separators=(",", ":")).encode("utf-8")


def _auth_headers() -> Dict[str, str]:
    """Build the optional bearer-token header for management endpoints."""
    if API_TOKEN:
        return {"Authorization": f"Bearer {API_TOKEN}"}
    return {}


# ---------------------------------------------------------------------------
# Async variant - httpx.
# ---------------------------------------------------------------------------
async def register_webhook_async(
    label: str,
    default_response_mode: Optional[ResponseMode] = "async",
    conversation_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Create a webhook registration and return the response (includes secret).

    The ``secret`` is only present on the create response - store it securely.
    """
    import httpx

    body: Dict[str, Any] = {
        "agentId": AGENT_ID,
        "label": label,
        "conversationId": conversation_id,
        "defaultResponseMode": default_response_mode,
    }
    async with httpx.AsyncClient(base_url=BASE_URL, timeout=30.0) as client:
        response = await client.post(
            "/api/webhooks/registrations",
            json=body,
            headers=_auth_headers(),
        )
        response.raise_for_status()
        return response.json()


async def send_inbound_async(
    webhook_id: str,
    secret: str,
    message: str,
    response_mode: Optional[ResponseMode] = None,
    agent_action: Optional[bool] = None,
    callback_url: Optional[str] = None,
    poll_timeout: float = 120.0,
    poll_interval: float = 2.0,
) -> Dict[str, Any]:
    """Send an inbound webhook delivery (async transport) and return the result.

    For ``sync`` mode the returned dict is the inline 200 response. For ``async``
    and ``callback`` modes it is the 202 ``WebhookAcceptedResponse``; ``async``
    additionally polls the run to completion and returns the final run object.
    """
    import httpx

    payload: Dict[str, Any] = {
        "message": message,
        "responseMode": response_mode,
        "agentAction": agent_action,
        "callbackUrl": callback_url,
    }
    body: bytes = encode_body(payload)
    headers: Dict[str, str] = {
        "Content-Type": "application/json",
        SIGNATURE_HEADER: compute_signature(secret, body),
    }

    async with httpx.AsyncClient(base_url=BASE_URL, timeout=poll_timeout + 30.0) as client:
        response = await client.post(
            f"/api/webhooks/{AGENT_ID}/{webhook_id}",
            content=body,  # send the exact signed bytes
            headers=headers,
        )
        response.raise_for_status()

        # Sync mode returns the full agent response inline with 200.
        if response.status_code == 200:
            return response.json()

        # Async / callback: 202 WebhookAcceptedResponse { runId, pollUrl, conversationId }.
        accepted: Dict[str, Any] = response.json()

        # Only async mode is polled here; callback mode is delivered out-of-band.
        if response_mode == "callback":
            return accepted

        run_id: str = accepted["runId"]
        deadline: float = time.monotonic() + poll_timeout
        while time.monotonic() < deadline:
            run_response = await client.get(
                f"/api/webhooks/runs/{run_id}",
                headers=_auth_headers(),
            )
            run_response.raise_for_status()
            run: Dict[str, Any] = run_response.json()
            status: str = str(run.get("status", "")).lower()
            if status in ("completed", "succeeded", "failed", "faulted", "cancelled"):
                return run
            await asyncio.sleep(poll_interval)

        raise TimeoutError(f"Run {run_id} did not complete within {poll_timeout}s")


# ---------------------------------------------------------------------------
# Sync variant - requests.
# ---------------------------------------------------------------------------
def register_webhook_sync(
    label: str,
    default_response_mode: Optional[ResponseMode] = "sync",
    conversation_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Create a webhook registration (sync transport) and return the response."""
    import requests

    body: Dict[str, Any] = {
        "agentId": AGENT_ID,
        "label": label,
        "conversationId": conversation_id,
        "defaultResponseMode": default_response_mode,
    }
    response = requests.post(
        f"{BASE_URL}/api/webhooks/registrations",
        json=body,
        headers=_auth_headers(),
        timeout=30.0,
    )
    response.raise_for_status()
    return response.json()


def send_inbound_sync(
    webhook_id: str,
    secret: str,
    message: str,
    response_mode: Optional[ResponseMode] = None,
    agent_action: Optional[bool] = None,
    callback_url: Optional[str] = None,
    poll_timeout: float = 120.0,
    poll_interval: float = 2.0,
) -> Dict[str, Any]:
    """Send an inbound webhook delivery (sync transport) and return the result."""
    import requests

    payload: Dict[str, Any] = {
        "message": message,
        "responseMode": response_mode,
        "agentAction": agent_action,
        "callbackUrl": callback_url,
    }
    body: bytes = encode_body(payload)
    headers: Dict[str, str] = {
        "Content-Type": "application/json",
        SIGNATURE_HEADER: compute_signature(secret, body),
    }

    response = requests.post(
        f"{BASE_URL}/api/webhooks/{AGENT_ID}/{webhook_id}",
        data=body,  # send the exact signed bytes
        headers=headers,
        timeout=poll_timeout + 30.0,
    )
    response.raise_for_status()

    if response.status_code == 200:
        return response.json()

    accepted: Dict[str, Any] = response.json()
    if response_mode == "callback":
        return accepted

    run_id: str = accepted["runId"]
    deadline: float = time.monotonic() + poll_timeout
    while time.monotonic() < deadline:
        run_response = requests.get(
            f"{BASE_URL}/api/webhooks/runs/{run_id}",
            headers=_auth_headers(),
            timeout=30.0,
        )
        run_response.raise_for_status()
        run: Dict[str, Any] = run_response.json()
        status: str = str(run.get("status", "")).lower()
        if status in ("completed", "succeeded", "failed", "faulted", "cancelled"):
            return run
        time.sleep(poll_interval)

    raise TimeoutError(f"Run {run_id} did not complete within {poll_timeout}s")


# ---------------------------------------------------------------------------
# Helpers to obtain a webhook id + secret.
# ---------------------------------------------------------------------------
def resolve_secret() -> Optional[str]:
    """Return the signing secret from the environment, if present."""
    return os.environ.get(SECRET_ENV_VAR)


async def ensure_registration_async(label: str) -> Tuple[str, str]:
    """Register a webhook and return ``(webhook_id, secret)``.

    Reuses ``BOTNEXUS_WEBHOOK_SECRET`` and ``BOTNEXUS_WEBHOOK_ID`` from the
    environment when both are available, otherwise creates a fresh registration.
    """
    env_secret: Optional[str] = resolve_secret()
    env_id: Optional[str] = os.environ.get("BOTNEXUS_WEBHOOK_ID")
    if env_secret and env_id:
        return env_id, env_secret

    registration: Dict[str, Any] = await register_webhook_async(label)
    webhook_id: str = registration["id"]
    secret: str = registration["secret"]
    print(f"Registered webhook {webhook_id}; url={registration.get('url')}")
    return webhook_id, secret


# ---------------------------------------------------------------------------
# Demo entrypoint.
# ---------------------------------------------------------------------------
async def _demo() -> int:
    """Exercise both transports across all three response modes."""
    # --- Async transport (httpx): register + async and callback modes. ---
    webhook_id, secret = await ensure_registration_async(
        label="python-example-async"
    )

    print("\n[async/httpx] responseMode=async (poll to completion)")
    async_result = await send_inbound_async(
        webhook_id,
        secret,
        message="Hello from the async httpx variant.",
        response_mode="async",
    )
    print(json.dumps(async_result, indent=2))

    print("\n[async/httpx] responseMode=callback (delivered out-of-band)")
    callback_result = await send_inbound_async(
        webhook_id,
        secret,
        message="Hello via callback mode.",
        response_mode="callback",
        callback_url="https://example.invalid/botnexus/callback",
    )
    print(json.dumps(callback_result, indent=2))

    # --- Sync transport (requests): sync mode returns inline. ---
    print("\n[sync/requests] responseMode=sync (inline 200)")
    sync_result = send_inbound_sync(
        webhook_id,
        secret,
        message="Hello from the sync requests variant.",
        response_mode="sync",
    )
    print(json.dumps(sync_result, indent=2))

    return 0


def main() -> int:
    """Run the demo, printing a friendly message on connection failure."""
    try:
        return asyncio.run(_demo())
    except Exception as exc:  # noqa: BLE001 - top-level demo guard
        print(f"Demo failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        print(
            "Ensure the gateway is running and BOTNEXUS_BASE_URL / "
            "BOTNEXUS_AGENT_ID / BOTNEXUS_API_TOKEN are set correctly.",
            file=sys.stderr,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

// webhook-sender.js
//
// Minimal, dependency-free BotNexus webhook sender for Node.js 18+.
// Uses the built-in global `fetch` and the native `node:crypto` module —
// no axios, no node-fetch, nothing to install.
//
// It demonstrates:
//   1. HMAC-SHA256 signing of the EXACT raw request body bytes.
//   2. Posting to POST api/webhooks/{agentId}/{webhookId}.
//   3. Async mode: reading the returned pollUrl and polling
//      GET api/webhooks/runs/{runId} until the agent run completes.
//
// The signing secret is read from the BOTNEXUS_WEBHOOK_SECRET env var
// (format: whsec_<64 hex chars>). Never hard-code it.
//
// Usage:
//   BOTNEXUS_WEBHOOK_SECRET=whsec_... \
//   node webhook-sender.js \
//     --base http://localhost:5000 \
//     --agent my-agent \
//     --webhook 1a2b3c \
//     --message "Hello from the webhook example"

import { createHmac } from 'node:crypto';

// ---------------------------------------------------------------------------
// HMAC signing
// ---------------------------------------------------------------------------
//
// The server computes HMAC-SHA256 over the raw request-body bytes using the
// secret's UTF-8 bytes, then compares against the header value
//   X-BotNexus-Signature-256: sha256=<lowercase hex>
// We MUST sign the exact string we send, so serialize the body ONCE and reuse
// that same string for both signing and the request body.
function signBody(secret, rawBody) {
  const digest = createHmac('sha256', secret).update(rawBody, 'utf8').digest('hex');
  return `sha256=${digest}`;
}

// ---------------------------------------------------------------------------
// Browser / Web Crypto alternative
// ---------------------------------------------------------------------------
//
// In a browser (or any Web Crypto environment) there is no node:crypto.
// Use the async SubtleCrypto API instead. Equivalent implementation:
//
//   async function signBodyWebCrypto(secret, rawBody) {
//     const enc = new TextEncoder();
//     const key = await crypto.subtle.importKey(
//       'raw', enc.encode(secret),
//       { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
//     const sigBuf = await crypto.subtle.sign('HMAC', key, enc.encode(rawBody));
//     const hex = [...new Uint8Array(sigBuf)]
//       .map((b) => b.toString(16).padStart(2, '0')).join('');
//     return `sha256=${hex}`;
//   }
//
// Note: browsers cannot set the Host/Origin-restricted CORS headers freely,
// so a real browser client usually posts through a same-origin proxy.

// ---------------------------------------------------------------------------
// Arg parsing (tiny, no dependencies)
// ---------------------------------------------------------------------------
function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i]?.replace(/^--/, '');
    if (key) args[key] = argv[i + 1];
  }
  return args;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const base = (args.base ?? 'http://localhost:5000').replace(/\/$/, '');
  const agentId = args.agent;
  const webhookId = args.webhook;
  const message = args.message ?? 'Hello from the BotNexus webhook example';
  const responseMode = args.mode ?? 'async';

  const secret = process.env.BOTNEXUS_WEBHOOK_SECRET;
  if (!secret) {
    console.error('Missing BOTNEXUS_WEBHOOK_SECRET environment variable.');
    process.exit(1);
  }
  if (!agentId || !webhookId) {
    console.error('Usage: node webhook-sender.js --agent <agentId> --webhook <webhookId> [--message ...] [--mode async|sync|callback] [--base <url>]');
    process.exit(1);
  }

  // Serialize the body ONCE and sign that exact string.
  const rawBody = JSON.stringify({
    message,
    responseMode,      // "async" | "sync" | "callback" | null
    agentAction: true, // false = record only, no agent run
    callbackUrl: null, // required only for callback mode
  });

  const signature = signBody(secret, rawBody);
  const url = `${base}/api/webhooks/${encodeURIComponent(agentId)}/${encodeURIComponent(webhookId)}`;

  console.log(`POST ${url}`);
  const res = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-BotNexus-Signature-256': signature,
    },
    body: rawBody,
  });

  if (res.status === 401) {
    console.error('401 Unauthorized — signature rejected. Check the secret matches the registration.');
    console.error(await res.text());
    process.exit(1);
  }

  // Sync mode returns 200 with the full response inline.
  if (res.status === 200) {
    console.log('Sync response:');
    console.log(JSON.stringify(await res.json(), null, 2));
    return;
  }

  // Async / callback modes return 202 { runId, pollUrl, conversationId }.
  if (res.status !== 202) {
    console.error(`Unexpected status ${res.status}`);
    console.error(await res.text());
    process.exit(1);
  }

  const accepted = await res.json();
  console.log(`Accepted: runId=${accepted.runId} conversationId=${accepted.conversationId}`);

  if (responseMode !== 'async') {
    // callback mode delivers the result to callbackUrl; nothing to poll here.
    console.log(`pollUrl: ${accepted.pollUrl}`);
    return;
  }

  // Poll GET api/webhooks/runs/{runId} until the run completes.
  // pollUrl may be relative; resolve against the base.
  const pollUrl = new URL(accepted.pollUrl, base).toString();
  console.log(`Polling ${pollUrl} ...`);

  const deadline = Date.now() + 120_000; // agent runs can take 30-120s
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 2000));
    const pollRes = await fetch(pollUrl, { headers: { Accept: 'application/json' } });
    if (!pollRes.ok) {
      console.error(`Poll failed: ${pollRes.status}`);
      continue;
    }
    const run = await pollRes.json();
    console.log(`  status=${run.status}`);
    const status = String(run.status ?? '').toLowerCase();
    if (status === 'completed' || status === 'succeeded' || run.agentResponse) {
      console.log('Agent response:');
      console.log(run.agentResponse ?? '(no response body)');
      return;
    }
    if (status === 'failed' || status === 'error') {
      console.error('Run failed.');
      console.error(JSON.stringify(run, null, 2));
      process.exit(1);
    }
  }

  console.error('Timed out waiting for the run to complete.');
  process.exit(1);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});

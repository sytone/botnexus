import fs from 'node:fs';
import { spawn } from 'node:child_process';

const home = '/evidence/home';
const data = '/evidence/data';
const output = '/evidence-output';
const catalog = '/evidence/catalog.json';
const startedUtc = new Date().toISOString();
let gateway;

const remove = path => fs.rmSync(path, { recursive: true, force: true });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitForGateway() {
  for (let attempt = 0; attempt < 120; attempt += 1) {
    if (gateway.exitCode !== null) throw new Error(`Gateway exited before readiness with code ${gateway.exitCode}.`);
    try {
      const response = await fetch('http://127.0.0.1:5000/health', { signal: AbortSignal.timeout(2000) });
      if (response.ok) return;
    } catch {}
    await sleep(250);
  }
  throw new Error('Gateway failed to become healthy inside the evidence container.');
}

async function stopGateway() {
  if (!gateway || gateway.exitCode !== null) return true;
  gateway.kill('SIGTERM');
  for (let attempt = 0; attempt < 50 && gateway.exitCode === null; attempt += 1) await sleep(100);
  if (gateway.exitCode === null) gateway.kill('SIGKILL');
  for (let attempt = 0; attempt < 50 && gateway.exitCode === null; attempt += 1) await sleep(100);
  return gateway.exitCode !== null;
}

try {
  fs.mkdirSync(home, { recursive: true });
  fs.mkdirSync(data, { recursive: true });
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(catalog, JSON.stringify({ scripts: { SLOW_STREAM: [
    { type: 'text_delta', delta: 'This', delayMs: 1500 },
    { type: 'text_delta', delta: ' is', delayMs: 1500 },
    { type: 'text_delta', delta: ' deterministic', delayMs: 1500 },
    { type: 'text_delta', delta: ' UI', delayMs: 1500 },
    { type: 'text_delta', delta: ' evidence.', delayMs: 1500 },
    { type: 'text_end' }, { type: 'done', stopReason: 'stop' }
  ] } }, null, 2));
  fs.writeFileSync(`${home}/config.json`, JSON.stringify({
    gateway: { listenUrl: 'http://127.0.0.1:5000', defaultAgentId: 'evidence-agent', extensionLoader: { enabled: true } },
    providers: { 'integration-mock': { api: 'integration-mock', baseUrl: catalog, defaultModel: 'integration-mock-echo', enabled: true } },
    agents: { 'evidence-agent': { displayName: 'Evidence Agent', provider: 'integration-mock', model: 'integration-mock-echo', enabled: true } }
  }, null, 2));
  const env = { ...process.env, BOTNEXUS_HOME: home, BOTNEXUS_DATA_DIR: data, BOTNEXUS_EXTENSIONS_PATH: '/app/extensions', BOTNEXUS_MOCK_CATALOG: catalog, ASPNETCORE_URLS: 'http://127.0.0.1:5000', BOTNEXUS_EVIDENCE_STARTED_UTC: startedUtc };
  gateway = spawn('dotnet', ['/app/publish/BotNexus.Gateway.Api.dll'], { env, stdio: ['ignore', fs.openSync('/evidence/gateway.stdout.log', 'a'), fs.openSync('/evidence/gateway.stderr.log', 'a')] });
  await waitForGateway();
  process.env.BOTNEXUS_EVIDENCE_STARTED_UTC = startedUtc;
  await import('./capture.mjs');
  const gatewayStopped = await stopGateway();
  remove(home); remove(data); remove(catalog);
  const manifestPath = `${output}/evidence.json`;
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  manifest.cleanup.gatewayStopped = gatewayStopped;
  manifest.cleanup.temporaryDataRemoved = !fs.existsSync(home) && !fs.existsSync(data) && !fs.existsSync(catalog);
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
} finally {
  await stopGateway();
  remove(home); remove(data); remove(catalog);
}

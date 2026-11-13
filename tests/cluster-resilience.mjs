import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

assert.equal(process.env.TOURBOOKING_ISOLATED_TESTS, '1', 'Isolated-test opt-in required');
const project = 'tourbooking-hybrid-verification';
const base = 'http://127.0.0.1:18083';
const config = Object.fromEntries(readFileSync('artifacts/hybrid-verification/test.env', 'utf8')
    .split(/\r?\n/).filter(line => line.includes('=')).map(line => {
        const index = line.indexOf('='); return [line.slice(0, index), line.slice(index + 1)];
    }));
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', timeout: 30000 }).trim();
const container = service => {
    assert.ok(['mongodb-a', 'mongodb-b', 'mongodb-c', 'rabbitmq-a'].includes(service));
    const id = docker('ps', '-a', '--filter', `label=com.docker.compose.project=${project}`,
        '--filter', `label=com.docker.compose.service=${service}`, '--format', '{{.ID}}');
    assert.match(id, /^[a-f0-9]{12}$/); return id;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function eventually(check, description) {
    const deadline = Date.now() + 120000;
    let lastError;
    do {
        try { if (await check()) return; } catch (error) { lastError = error.message; }
        await pause(1000);
    } while (Date.now() < deadline);
    throw new Error(`${description}: ${lastError ?? 'condition not met'}`);
}
await eventually(async () => {
    const page = await fetch(`${base}/Account/Register`, { signal: AbortSignal.timeout(10000) });
    await page.arrayBuffer();
    return page.status === 200;
}, 'Identity route startup');
await eventually(async () => {
    const nodes = new Set();
    for (let sample = 0; sample < 8; sample++) {
        const response = await fetch(`${base}/api/v1/auth/me`, { signal: AbortSignal.timeout(10000) });
        await response.arrayBuffer();
        if (response.status !== 401) return false;
        const node = response.headers.get('x-service-node');
        if (node) nodes.add(node);
    }
    return nodes.size >= 1;
}, 'Identity API reachable through Kong');
const login = await fetch(`${base}/api/v1/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: config.ADMIN_EMAIL, password: config.HYBRID_ADMIN_PASSWORD }),
    signal: AbortSignal.timeout(15000)
});
assert.equal(login.status, 200);
const token = (await login.json()).data.access_token;
assert.ok(token);
async function readable() {
    for (const path of ['/api/v1/chat', '/api/v1/notifications']) {
        const response = await fetch(base + path, { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15000) });
        if (response.status !== 200 || !(await response.json()).success) return false;
    }
    return true;
}
await eventually(readable, 'Cluster application baseline');
const primary = docker('exec', container('mongodb-b'), 'mongosh', '--quiet', '--eval', 'print(db.hello().primary)');
assert.match(primary, /^mongodb-[abc]:27017$/);
const primaryId = container(primary.split(':')[0]);
const start = Date.now();
docker('stop', '--time', '2', primaryId);
try { await eventually(readable, 'Chat/notification reconnect after primary loss'); }
finally { docker('start', primaryId); }
const recoveryMs = Date.now() - start;
const broker = container('rabbitmq-a');
docker('stop', '--time', '2', broker);
try { await eventually(readable, 'Stored chat/notification remain readable during broker loss'); }
finally { docker('start', broker); }
writeFileSync('artifacts/hybrid-verification/cluster-apps.json', JSON.stringify({
    timestamp_utc: new Date().toISOString(), mongo_primary_before: primary, mongo_read_recovery_ms: recoveryMs,
    application_read_recovery: true, reads_during_broker_loss: true,
    limitation: 'Single host; mutation/deduplication checked separately by integration suite, not this read-only fault check'
}, null, 2));
console.log('PASS cluster-backed chat/notification baseline, Mongo primary loss and broker-independent reads');

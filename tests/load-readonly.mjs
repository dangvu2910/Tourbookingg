import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';

// Deliberately fixed to the isolated verification entry; never load-test live by accident.
const base = 'http://127.0.0.1:18083';
const concurrency = Number(process.env.LOAD_CONCURRENCY ?? 4);
const durationSeconds = Number(process.env.LOAD_SECONDS ?? 20);
const maxP95Ms = Number(process.env.LOAD_MAX_P95_MS ?? 2000);
const maxErrorRate = Number(process.env.LOAD_MAX_ERROR_RATE ?? 0.01);
assert.ok(Number.isInteger(concurrency) && concurrency > 0 && concurrency <= 32);
assert.ok(Number.isFinite(durationSeconds) && durationSeconds > 0 && durationSeconds <= 300);
assert.ok(Number.isFinite(maxP95Ms) && maxP95Ms > 0);
assert.ok(Number.isFinite(maxErrorRate) && maxErrorRate >= 0 && maxErrorRate <= 1);
const latencies = [], nodes = new Set();
let errors = 0;
const start = performance.now(), deadline = start + durationSeconds * 1000;
await Promise.all(Array.from({ length: concurrency }, async () => {
    while (performance.now() < deadline) {
        const requestStart = performance.now();
        try {
            const response = await fetch(`${base}/api/v1/catalog`, { signal: AbortSignal.timeout(10000) });
            const json = await response.json();
            if (response.status !== 200 || json.success !== true) errors++;
            const node = response.headers.get('x-service-node');
            if (node) nodes.add(node);
        } catch { errors++; }
        latencies.push(performance.now() - requestStart);
    }
}));
latencies.sort((a, b) => a - b);
const quantile = p => latencies[Math.max(0, Math.ceil(latencies.length * p) - 1)] ?? 0;
const seconds = (performance.now() - start) / 1000;
const result = { timestamp_utc: new Date().toISOString(), base, concurrency, seconds,
    requests: latencies.length, errors, error_rate: errors / latencies.length,
    requests_per_second: latencies.length / seconds, p50_ms: quantile(0.50),
    p95_ms: quantile(0.95), p99_ms: quantile(0.99), nodes: [...nodes],
    thresholds: { max_p95_ms: maxP95Ms, max_error_rate: maxErrorRate },
    limitation: 'Read-only catalog baseline on one Docker host; not a booking throughput or autoscaling test' };
mkdirSync('artifacts/load-verification', { recursive: true });
writeFileSync('artifacts/load-verification/result.json', JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
assert.ok(result.requests > 0 && result.p95_ms <= maxP95Ms, 'p95 threshold exceeded');
assert.ok(result.error_rate <= maxErrorRate, 'Error rate threshold exceeded');

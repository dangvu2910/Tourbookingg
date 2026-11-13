import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';

const project = 'tourbooking-hybrid-verification';
const base = 'http://127.0.0.1:18083';
if (process.env.TOURBOOKING_ISOLATED_TESTS !== '1') throw new Error('Explicit isolated-test opt-in required.');
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', timeout: 30000 }).trim();
function nodes(service) {
    assert.ok(['identity', 'catalog', 'booking', 'customer', 'notification', 'web-router', 'gateway', 'rabbitmq', 'sqlserver', 'mongodb'].includes(service));
    const names = docker('ps', '-a', '--filter', `label=com.docker.compose.project=${project}`,
        '--filter', `label=com.docker.compose.service=${service}`, '--format', '{{.Names}}').split('\n').filter(Boolean);
    assert.ok(names.length, `Missing isolated ${service}`);
    for (const name of names) assert.ok(name.startsWith(project + '-'));
    return names;
}
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const cookies = new Map();
async function request(path, body, bearer) {
    const headers = { Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; ') };
    if (bearer) { delete headers.Cookie; headers.Authorization = `Bearer ${bearer}`; }
    if (body && !(body instanceof URLSearchParams)) headers['Content-Type'] = 'application/json';
    const started = performance.now();
    const response = await fetch(new URL(path, base), { method: body ? 'POST' : 'GET', body,
        headers, redirect: 'manual', signal: AbortSignal.timeout(12000) });
    for (const value of response.headers.getSetCookie()) {
        const pair = value.split(';')[0], index = pair.indexOf('=');
        cookies.set(pair.slice(0, index), pair.slice(index + 1));
    }
    return { status: response.status, text: await response.text(), location: response.headers.get('location'),
        node: response.headers.get('x-service-node'),
        ms: Math.round(performance.now() - started) };
}
const decode = value => value.replace(/&#x([a-f0-9]+);/gi, (_, n) => String.fromCodePoint(parseInt(n, 16)))
    .replace(/&#([0-9]+);/g, (_, n) => String.fromCodePoint(Number(n))).replaceAll('&amp;', '&').replaceAll('&quot;', '"');
function fields(html) {
    const values = {};
    for (const tag of html.match(/<input\b[^>]*>/g) ?? []) {
        const name = tag.match(/name="([^"]+)"/)?.[1];
        if (name) values[name] = decode(tag.match(/value="([^"]*)"/)?.[1] ?? '');
    }
    return values;
}
const routes = [
    ['home', '/', 'web-router', false, 200],
    ['login', '/Account/Login', 'identity', false, 302],
    ['catalog', '/api/v1/catalog', 'catalog', true, 200],
    ['tours', '/Tours', 'catalog', false, 200],
    ['bookings', '/api/v1/bookings', 'booking', true, 200],
    ['account', '/Account/Bookings', 'booking', false, 200],
    ['contact', '/Home/Contact', 'customer', false, 200],
    ['chat', '/api/v1/chat', 'customer', true, 200],
    ['notifications', '/api/v1/notifications', 'notification', true, 200]
];
async function matrix(down) {
    return Promise.all(routes.map(async ([name, path, owner, direct, status]) => {
        const unavailable = down === owner || down === 'gateway' || down === 'web-router' && !direct
            || down === 'sqlserver' && ['identity', 'catalog', 'booking', 'customer'].includes(owner)
            || down === 'mongodb' && ['customer', 'notification'].includes(owner);
        const expected = unavailable ? [502, 503, 504] : [status];
        try {
            const response = await request(path);
            return { name, status: response.status, ms: response.ms, passed: expected.includes(response.status), expected };
        } catch (error) { return { name, passed: false, error: error.name, expected }; }
    }));
}
async function eventually(check, description, seconds = 60) {
    const deadline = Date.now() + seconds * 1000;
    let last;
    do {
        try { last = await check(); if (last === true) return; } catch (error) { last = error.message; }
        await pause(1000);
    } while (Date.now() < deadline);
    throw new Error(`${description}: ${JSON.stringify(last)}`);
}
async function expectMatrix(down) {
    let checks;
    await eventually(async () => {
        checks = await matrix(down);
        return checks.every(check => check.passed) || checks.filter(check => !check.passed);
    }, `Routes with ${down ?? 'all services up'}`);
    return checks;
}
const report = { project, storage: 'Isolated SQL Server + MongoDB', distribution: {}, scenarios: [] };
const stopped = new Set();
function stop(names) { for (const name of names) { docker('stop', '--time', '2', name); stopped.add(name); } }
function start(names) { for (const name of names) { docker('start', name); stopped.delete(name); } }
const date = new Date(Date.now() + (500 + Math.floor(Math.random() * 1000)) * 86400000).toISOString().slice(0, 10);
let email;
async function book() {
    const catalog = JSON.parse((await request('/api/v1/catalog')).text).data[0];
    const page = await request(`/Home/Booking?tourId=${catalog.tour_id}`);
    const form = fields(page.text);
    const created = await request('/Home/Booking', new URLSearchParams({ ...form, TourId: String(catalog.tour_id),
        FullName: 'Hybrid resilience test', Email: email, PhoneNumber: '0901234567', TravelDate: date, GuestCount: '1' }));
    assert.equal(created.status, 302, created.text.slice(0, 200));
    const id = Number(new URL(created.location, base).searchParams.get('bookingId'));
    assert.ok(id > 0);
    return id;
}
try {
    await eventually(async () => (await request('/api/v1/catalog')).status === 200, 'Hybrid stack startup', 150);
    email = `hybrid-resilience-${Date.now()}@example.test`;
    const password = 'Hybrid!Customer123';
    let registration;
    await eventually(async () => {
        registration = await request('/Account/Register');
        return registration.status === 200;
    }, 'Identity MVC route startup', 150);
    const registered = await request('/Account/Register', new URLSearchParams({ ...fields(registration.text),
        Email: email, Password: password, ConfirmPassword: password }));
    assert.equal(registered.status, 302);
    const loggedIn = await request('/api/v1/auth/login', JSON.stringify({ email, password }));
    assert.equal(loggedIn.status, 200);
    const token = JSON.parse(loggedIn.text).data.access_token;
    assert.ok(token);
    report.scenarios.push({ name: 'baseline', checks: await expectMatrix(null) });

    // One instance per service locally.
    for (const [name, path] of [['web-router', '/'], ['identity', '/api/v1/auth/me'], ['catalog', '/api/v1/catalog'],
        ['booking', '/api/v1/bookings'], ['customer', '/api/v1/chat'], ['notification', '/api/v1/notifications']]) {
        const response = await request(path);
        assert.equal(response.status, 200, `${name} must answer through Kong`);
        report.distribution[name] = { nodes: response.node ? [response.node] : [] };
    }
    console.log('PASS every service answers through the single Kong entry');

    const bookingId = await book();
    const oldPage = await request(`/Home/Payment?bookingId=${bookingId}`);
    await pause(6000);
    const paymentForm = new URLSearchParams({ ...fields(oldPage.text), bookingId: String(bookingId), transactionCode: 'HYBRID-TRANSFER' });
    assert.equal((await request('/Home/ConfirmPayment', paymentForm)).status, 302);
    assert.equal((await request('/Home/ConfirmPayment', paymentForm)).status, 302);
    assert.equal(JSON.parse((await request(`/api/bookings/${bookingId}`)).text).status, 'Processing');
    const acknowledged = await request(`/Home/Payment?bookingId=${bookingId}`);
    assert.ok(!acknowledged.text.includes('alt="QR thanh toán"'));
    console.log('PASS SQL booking/payment acknowledgement after background update and duplicate submit');

    // Each service runs once: stopping it is a full outage. Dependent routes must fail fast, others keep working.
    for (const service of ['catalog', 'booking', 'customer', 'notification', 'identity', 'web-router', 'gateway', 'sqlserver', 'mongodb']) {
        const replicas = nodes(service);
        stop(replicas);
        try {
            const checks = await expectMatrix(service);
            if (service === 'identity') assert.equal((await request('/api/v1/bookings', undefined, token)).status, 503);
            report.scenarios.push({ name: `all-${service}-down`, checks });
            console.log(`PASS total ${service} outage: bounded dependency errors; unrelated routes continue`);
        } finally { start(replicas); }
        await expectMatrix(null);
        console.log(`PASS ${service} recovered`);
    }
    const broker = nodes('rabbitmq');
    stop(broker);
    let pendingBooking;
    try {
        report.scenarios.push({ name: 'rabbitmq-down', checks: await expectMatrix(null) });
        pendingBooking = await book();
        assert.equal(JSON.parse((await request(`/api/bookings/${pendingBooking}`)).text).bookingId, pendingBooking);
        console.log('PASS RabbitMQ outage: notification reads and SQL booking writes remain available');
    } finally { start(broker); }
    await eventually(async () => JSON.parse((await request('/api/notifications')).text)
        .some(item => item.resourceId === String(pendingBooking) && item.type === 'booking.created'), 'SQL outbox recovery into Mongo notification', 90);
    report.outboxRecovered = true;
    console.log('PASS queued SQL outbox delivered to MongoDB after RabbitMQ restart');
    report.success = true;
} catch (error) {
    report.error = error.message;
    process.exitCode = 1;
    console.error(error.stack);
} finally {
    for (const name of stopped) { try { docker('start', name); } catch {} }
    mkdirSync('artifacts/hybrid-verification', { recursive: true });
    writeFileSync('artifacts/hybrid-verification/resilience.json', JSON.stringify(report, null, 2));
}

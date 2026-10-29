import assert from 'node:assert/strict';

const base = process.env.TOURBOOKING_CONCURRENCY_URL;
if (process.env.TOURBOOKING_ISOLATED_TESTS !== '1' || !base || new URL(base).port === '8080')
    throw new Error('Run only against an explicitly configured isolated test stack, never the normal port 8080.');
const adminEmail = process.env.TOURBOOKING_TEST_ADMIN_EMAIL;
const adminPassword = process.env.TOURBOOKING_TEST_ADMIN_PASSWORD;
if (!adminEmail || !adminPassword) throw new Error('Missing isolated test admin credentials.');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const decode = value => value.replace(/&#x([0-9a-f]+);/gi, (_, n) => String.fromCodePoint(parseInt(n, 16)))
    .replace(/&#([0-9]+);/g, (_, n) => String.fromCodePoint(Number(n)))
    .replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&#39;/g, "'");
function inputs(html) {
    const values = {};
    for (const tag of html.match(/<input\b[^>]*>/g) || []) {
        const name = tag.match(/\bname="([^"]*)"/)?.[1];
        const value = tag.match(/\bvalue="([^"]*)"/)?.[1] || '';
        if (name) values[decode(name)] = decode(value);
    }
    return values;
}
class Client {
    cookies = new Map();
    async request(path, body, accept = 'text/html') {
        const headers = { Accept: accept, Cookie: [...this.cookies].map(([k,v]) => k+'='+v).join('; ') };
        if (body) headers['Content-Type'] = 'application/x-www-form-urlencoded';
        const response = await fetch(new URL(path, base), { method: body ? 'POST' : 'GET',
            headers, body: body ? new URLSearchParams(body) : undefined, redirect: 'manual', signal: AbortSignal.timeout(20000) });
        for (const cookie of response.headers.getSetCookie()) {
            const pair = cookie.split(';')[0], index = pair.indexOf('=');
            this.cookies.set(pair.slice(0,index), pair.slice(index+1));
        }
        return response;
    }
    async page(path) { const r = await this.request(path); assert.equal(r.status, 200, path); return r.text(); }
    async state(id) {
        const r = await this.request('/api/bookings/'+id, undefined, 'application/json');
        assert.equal(r.status, 200); return r.json();
    }
}
async function login(client, email, password) {
    const html = await client.page('/Account/Login');
    const r = await client.request('/Account/Login', { Email: email, Password: password,
        __RequestVerificationToken: inputs(html).__RequestVerificationToken });
    assert.equal(r.status, 302, 'Login');
}
async function form(client, action, id, page = '/Account/Bookings') {
    const html = await client.page(page);
    for (const block of html.match(/<form\b[^>]*>[\s\S]*?<\/form>/g) || []) {
        const href = decode(block.match(/\baction="([^"]*)"/)?.[1] || '');
        const values = inputs(block);
        const url = new URL(href, base);
        if ((url.pathname === action || url.pathname.startsWith(action+'/')) &&
            (values.bookingId === String(id) || url.pathname.endsWith('/'+id) || url.searchParams.get('id') === String(id))) {
            assert.ok(values.bookingVersion, 'Every mutation form must carry the original booking version');
            assert.ok(values.__RequestVerificationToken, 'CSRF token must remain enabled');
            return {href, values};
        }
    }
    throw new Error('Missing form '+action+' for booking '+id);
}
const submit = (client, f, extra = {}, accept) => client.request(f.href, {...f.values, ...extra}, accept);
async function conflict(response) {
    assert.equal(response.status, 409, await response.clone().text());
    assert.match(await response.text(), /Booking đã thay đổi/);
}
let check = new Client();
for (let attempt=0; attempt<90; attempt++) {
    try {
        const r = await check.request('/api/tours');
        const tours = r.status === 200 ? await r.json() : [];
        if (tours.length) break;
    } catch {}
    if (attempt === 89) throw new Error('Isolated stack did not become ready');
    await pause(1000);
}
const email = 'concurrency-'+Date.now()+'@example.test';
const password = 'Isolated!Password123';
const userA = new Client(), userB = new Client(), adminA = new Client(), adminB = new Client();
const registration = await userA.page('/Account/Register');
assert.equal((await userA.request('/Account/Register', { Email: email, Password: password, ConfirmPassword: password,
    __RequestVerificationToken: inputs(registration).__RequestVerificationToken })).status, 302);
await login(userB,email,password);
await login(adminA,adminEmail,adminPassword);
await login(adminB,adminEmail,adminPassword);
const tourResponse=await userA.request('/api/tours');
const tour=(await tourResponse.json())[0];
function dateAfter(days) { const d = new Date(); d.setUTCDate(d.getUTCDate()+days); return d.toISOString().slice(0,10); }
async function booking(ready = true) {
    const html = await userA.page('/Home/Booking?tourId='+tour.tourId);
    const values = inputs(html);
    const created = await userA.request('/Home/Booking', {
        TourId: String(tour.tourId), RequestId: values.RequestId, FullName: 'Isolated concurrency test',
        Email: email, PhoneNumber: '0901234567', GuestCount: '1', TravelDate: dateAfter(60),
        __RequestVerificationToken: values.__RequestVerificationToken
    });
    assert.equal(created.status,302,await created.clone().text());
    const location=created.headers.get('location');
    const id=Number(new URL(location,base).searchParams.get('bookingId'));
    assert.ok(id>0,location);
    if (ready) {
        const f=await form(userA,'/Home/ConfirmPayment',id,location);
        assert.equal((await submit(userA,f,{transactionCode:'ISOLATED-CHECK'})).status,302);
    }
    return id;
}
{
    const id=await booking(), a=await form(userA,'/Account/RescheduleBooking',id), b=await form(userB,'/Account/CancelBooking',id);
    assert.equal((await submit(userA,a,{newTravelDate:dateAfter(61),note:'First browser'})).status,302);
    await conflict(await submit(userB,b,{reason:'Stale second browser'}));
    const state=await userA.state(id); assert.notEqual(state.status,'Cancelled'); assert.ok(state.travelDate.startsWith(dateAfter(61)));
    const fresh=await form(userB,'/Account/CancelBooking',id);
    assert.equal((await submit(userB,fresh,{reason:'Explicit cancellation after reloading'})).status,302);
    assert.equal((await userA.state(id)).status,'Cancelled');
    console.log('PASS: same-account reschedule then stale cancel -> 409; reload then cancel -> success');
}
{
    const id=await booking(), a=await form(userA,'/Account/CancelBooking',id), b=await form(userB,'/Account/RescheduleBooking',id);
    assert.equal((await submit(userA,a,{reason:'First browser'})).status,302);
    await conflict(await submit(userB,b,{newTravelDate:dateAfter(62)}));
    assert.equal((await userA.state(id)).status,'Cancelled');
    console.log('PASS: same-account cancel then stale reschedule -> 409');
}
for (const first of ['approve','cancel']) {
    const id=await booking();
    const a=await form(adminA,'/Admin/ConfirmPaymentReceived',id,'/Admin/Bookings');
    const b=await form(adminB,'/Admin/UpdateBookingStatus',id,'/Admin/Bookings');
    if(first==='approve') {
        assert.equal((await submit(adminA,a)).status,302);
        await conflict(await submit(adminB,b));
        assert.equal((await userA.state(id)).status,'Confirmed');
    } else {
        assert.equal((await submit(adminB,b)).status,302);
        await conflict(await submit(adminA,a));
        assert.equal((await userA.state(id)).status,'Cancelled');
    }
    console.log('PASS: admin '+first+' wins; stale opposing action -> 409');
}
{
    const id=await booking(), a=await form(userA,'/Account/RescheduleBooking',id), b=await form(adminA,'/Admin/ConfirmPaymentReceived',id,'/Admin/Bookings');
    assert.equal((await submit(userA,a,{newTravelDate:dateAfter(63)})).status,302);
    await conflict(await submit(adminA,b));
    console.log('PASS: user reschedule invalidates stale admin approval');
}
{
    const id=await booking(), a=await form(adminA,'/Admin/ConfirmPaymentReceived',id,'/Admin/Bookings'), b=await form(userA,'/Account/CancelBooking',id);
    assert.equal((await submit(adminA,a)).status,302);
    await conflict(await submit(userA,b,{reason:'Stale user cancellation'}));
    assert.equal((await userA.state(id)).status,'Confirmed');
    console.log('PASS: admin approval invalidates stale user cancellation');
}
{
    const id=await booking(), a=await form(adminA,'/Admin/ConfirmPaymentReceived',id,'/Admin/Bookings'), b=await form(adminB,'/Admin/UpdateBookingStatus',id,'/Admin/Bookings');
    const responses=await Promise.all([submit(adminA,a),submit(adminB,b)]);
    assert.deepEqual(responses.map(r=>r.status).sort(),[302,409]);
    console.log('PASS: simultaneous approval/cancellation -> exactly one success and one 409');
}
{
    const id=await booking(), f=await form(userA,'/Account/CancelBooking',id);
    for(const version of ['', 'not-a-version']) await conflict(await submit(userA,f,{reason:'Invalid version',bookingVersion:version}));
    assert.notEqual((await userA.state(id)).status,'Cancelled');
    const csrf=await submit(userA,f,{reason:'Invalid CSRF',__RequestVerificationToken:'invalid'});
    assert.equal(csrf.status,400);
    const json=await submit(userA,f,{reason:'Invalid version',bookingVersion:''},'application/json');
    assert.equal(json.status,409); assert.equal((await json.json()).status,409);
    console.log('PASS: absent/invalid versions -> 409, invalid CSRF -> 400, JSON clients retain structured 409');
}
{
    const id=await booking(false), a=await form(userA,'/Account/RescheduleBooking',id);
    const b=await form(userB,'/Home/ConfirmPayment',id,'/Home/Payment?bookingId='+id);
    assert.equal((await submit(userA,a,{newTravelDate:dateAfter(64)})).status,302);
    await conflict(await submit(userB,b,{transactionCode:'STALE-PAYMENT'}));
    assert.notEqual((await userA.state(id)).status,'Processing');
    console.log('PASS: stale payment confirmation cannot overwrite a rescheduled booking');
}
console.log('All isolated HTTP concurrency checks passed. Production databases were not used.');
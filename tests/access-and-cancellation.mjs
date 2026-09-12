import assert from 'node:assert/strict';
const base = process.argv[2] || 'http://localhost:8081';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use an isolated local verification stack.');
if (!process.env.APP_ACCESS_TOKEN) throw Error('Set the access token used by the test stack.');
const auth = { Authorization: `Bearer ${process.env.APP_ACCESS_TOKEN}` };
const headers = { ...auth, 'X-Open-OCC': '1', 'Content-Type': 'application/json' };
assert.equal((await fetch(`${base}/api/campaigns`)).status, 401);
assert.equal((await fetch(`${base}/api/campaigns`, { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Blocked request' }) })).status, 403);
const create = await fetch(`${base}/api/campaigns`, { method: 'POST', headers, body: JSON.stringify({ name: 'Cancellation verification', synthetic: true }) });
assert.equal(create.status, 200);
const branch = await create.json();
const read = async () => {
  const r = await fetch(`${base}/api/branches/${branch.id}`, { headers: auth });
  assert.equal(r.status, 200); return r.json();
};
const initial = await read();
const abort = new AbortController();
const pending = fetch(`${base}/api/branches/${branch.id}/turns`, { method: 'POST', headers, body: JSON.stringify({ action: 'This action must be cancelled.', expectedCheckpointId: initial.checkpoint.id }), signal: abort.signal }).catch(e => e);
let started = false;
for (let i = 0; i < 25; i++) {
  if ((await read()).runs.some(r => r.status === 'running')) { started = true; break; }
  await new Promise(resolve => setTimeout(resolve, 20));
}
assert.ok(started, 'Generation should be persisted before fixture work begins');
abort.abort(); await pending;
let after;
for (let i = 0; i < 30; i++) {
  after = await read();
  if (after.runs.some(r => r.status === 'cancelled')) break;
  await new Promise(resolve => setTimeout(resolve, 100));
}
assert.ok(after.runs.some(r => r.status === 'cancelled'), 'Server records a cancelled run');
assert.equal(after.checkpoint.id, initial.checkpoint.id);
assert.equal(after.messages.length, initial.messages.length);
assert.deepEqual(after.state, initial.state);
console.log('PASS: access token required, cross-origin-style writes rejected, client cancellation recorded, no partial turn or checkpoint mutation.');

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
const base = process.argv[2] || 'http://localhost:8082';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use the isolated local scripted-model stack.');
async function call(path, value, expected = 200) {
  const response = await fetch(`${base}/api${path}`, { method: value === undefined ? 'GET' : 'POST', headers: { 'X-Open-OCC': '1', ...(value instanceof FormData ? {} : { 'Content-Type': 'application/json' }) }, body: value === undefined ? undefined : value instanceof FormData ? value : JSON.stringify(value) });
  const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
const branch = await call('/campaigns', { name: 'Skill shape repair HTTP verification', synthetic: false });
const source = 'Mara, a navigator, and her companion Lyra stand at Harbor on Day 7 at noon. Mara has two rations, Navigation F \u00b7 2/5, and Magic Talent F \u00b7 2/5. Mara trusts Lyra. Only Mara knows the silver key opens the archive. Cross the bay at low tide before dusk. Player: Pack two rations.';
const form = new FormData(); form.append('file', new Blob([source]), 'skill-repair.txt'); form.append('prepareOnly', 'true'); form.append('provider', 'deepseek'); form.append('model', 'scripted-skill-repair');
const job = await call(`/branches/${branch.id}/imports`, form, 202); assert.equal(job.status, 'prepared');
await call(`/imports/${job.id}/resume-processing`, {});
let draft;
for (let i = 0; i < 100; i++) {
  draft = await call(`/imports/${job.id}/draft`);
  if (draft.job.status === 'review') break;
  if (draft.job.status === 'paused') throw Error(draft.job.error);
  await new Promise(resolve => setTimeout(resolve, 250));
}
assert.equal(draft.job.status, 'review'); assert.equal(draft.job.processed, draft.job.total); assert.ok(draft.job.calls >= 2);
const proposals = await call(`/imports/${job.id}/proposals?category=skill&q=Magic%20Talent`);
assert.equal(proposals.total, 1); assert.equal(proposals.items[0].claim.subject, 'Magic Talent'); assert.equal(proposals.items[0].claim.value, 'F \u00b7 2/5');
assert.equal(draft.resume.state.skills['Magic Talent'], 'F \u00b7 2/5');
assert.equal(draft.approval.canApprove, true);
const bytes = Buffer.from(await (await fetch(`${base}/api/sources/${job.sourceId}/download`)).arrayBuffer());
assert.equal(createHash('sha256').update(bytes).digest('hex'), createHash('sha256').update(source).digest('hex'));
console.log('PASS: object skill value repaired into an evidence-supported string using exact field feedback; complete coverage, valid review readiness, and original bytes preserved over HTTP.');

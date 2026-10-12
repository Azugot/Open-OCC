import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
const base = process.argv[2] || 'http://localhost:8082';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use the isolated local test stack.');
async function call(path, value, method = value === undefined ? 'GET' : 'POST', status = 200) {
  const response = await fetch(`${base}/api${path}`, { method, headers: { 'X-Open-OCC': '1', ...(value instanceof FormData ? {} : { 'Content-Type': 'application/json' }) }, body: value === undefined ? undefined : value instanceof FormData ? value : JSON.stringify(value) });
  const text = await response.text(); assert.equal(response.status, status, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
const branch = await call('/campaigns', { name: 'Import reliability HTTP verification', synthetic: false });
for (const [name, content] of [['broken.json', '{broken'], ['missing.json', '{"messages":[{"role":"user"}]}'], ['wrong.json', '{"messages":[{"role":7,"content":"hi"}]}'], ['broken.docx', 'not a zip']]) {
  const form = new FormData(); form.append('file', new Blob([content]), name); form.append('prepareOnly', 'true'); form.append('provider', 'missing-provider');
  const job = await call(`/branches/${branch.id}/imports`, form, 'POST', 202);
  assert.equal(job.status, 'paused'); assert.equal(job.stage, 'normalize'); assert.equal(job.calls, 0);
  const bytes = Buffer.from(await (await fetch(`${base}/api/sources/${job.sourceId}/download`)).arrayBuffer());
  assert.equal(createHash('sha256').update(bytes).digest('hex'), createHash('sha256').update(content).digest('hex'));
}
const form = new FormData(); form.append('file', new Blob(['Mara waits at the Harbor.']), 'valid.txt'); form.append('prepareOnly', 'true'); form.append('provider', 'fixture'); form.append('destination', 'new'); form.append('timelineName', 'Imported timeline'); form.append('checkpointId', branch.headCheckpointId);
const prepared = await call(`/branches/${branch.id}/imports`, form, 'POST', 202);
assert.equal(prepared.status, 'prepared'); assert.notEqual(prepared.branchId, branch.id); assert.equal(prepared.calls, 0);
await call(`/imports/${prepared.id}/resume-processing`, {}, 'POST', 400);
assert.equal((await call(`/branches/${branch.id}`)).checkpoint.id, branch.headCheckpointId);
const exportData = await call(`/campaigns/${branch.campaignId}/export`);
await call('/campaigns/import', { export: { ...exportData, sources: null } }, 'POST', 400);
await call('/campaigns/import', { export: { ...exportData, branches: [...exportData.branches, exportData.branches[0]] } }, 'POST', 400);
await call('/campaigns/import', { export: { ...exportData, checkpoints: [] } }, 'POST', 400);
// Restore a synthetic legacy review with more than one candidate page.
const legacy = structuredClone(exportData);
legacy.jobs = [{ ...legacy.jobs.find(x => x.id === prepared.id), branchId: branch.id, expectedCheckpointId: branch.headCheckpointId, method: 'deterministic-v1', status: 'review' }];
legacy.facts = Array.from({ length: 45 }, (_, i) => ({ id: randomUUID(), branchId: branch.id, jobId: prepared.id, effectiveSequence: 1, text: `Legacy candidate ${i + 1}`, provenance: 'imported', reviewStatus: 'pending', visibility: 'public', kind: 'fact', confidence: 1, knownByJson: '[]' }));
legacy.sources = legacy.sources.filter(x => x.id === prepared.sourceId);
legacy.segments = legacy.segments.filter(x => x.jobId === prepared.id);
legacy.evidence = legacy.facts.map(x => ({ id: randomUUID(), factId: x.id, segmentId: legacy.segments[0].id }));
legacy.importSections = legacy.importSections.filter(x => x.jobId === prepared.id);
const restored = await call('/campaigns/import', { export: legacy, name: 'Legacy paging verification' });
const [legacyJob] = await call(`/branches/${restored.id}/imports`);
const pages = [];
for (let page = 0; page < 3; page++) {
  const data = await call(`/imports/${legacyJob.id}/candidates?page=${page}`);
  assert.equal(data.total, 45); assert.equal(data.items.length, page < 2 ? 20 : 5);
  pages.push(...data.items.map(x => x.fact.id));
}
assert.equal(new Set(pages).size, 45);
console.log(`PASS: malformed source preservation, provider-free preview, atomic new timeline, fixture rejection, malformed export rejection, and all 45 legacy candidates over HTTP. Browser legacy job: ${legacyJob.id}, branch: ${restored.id}`);

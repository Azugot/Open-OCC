// Run against a disposable backend whose LEMONADE/DEEPSEEK base URLs point to http://127.0.0.1:11450/v1.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
const base = process.argv[2] || 'http://127.0.0.1:5080';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use a disposable local stack.');
const headers = { 'Content-Type': 'application/json', 'X-Open-OCC': '1' };
if (process.env.APP_ACCESS_TOKEN) headers.Authorization = `Bearer ${process.env.APP_ACCESS_TOKEN}`;
async function call(path, data, method = data === undefined ? 'GET' : 'POST', expected = 200) {
  const response = await fetch(`${base}/api${path}`, { method, headers, body: data === undefined ? undefined : JSON.stringify(data) });
  const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
const calls = [];
let catalog = [{ id: 'small-chat', labels: ['reasoning'], downloaded: true }, { id: 'large-chat', name: 'Large chat', downloaded: true }, { id: 'image-only', labels: ['image'] }];
const server = createServer(async (req, res) => {
  const chunks = []; for await (const chunk of req) chunks.push(chunk);
  const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
  calls.push({ path: req.url, method: req.method, authorization: req.headers.authorization, body });
  res.setHeader('Content-Type', 'application/json');
  if (req.method === 'GET' && req.url === '/v1/models') return res.end(JSON.stringify({ object: 'list', data: catalog }));
  if (req.method !== 'POST' || req.url !== '/v1/chat/completions') { res.statusCode = 404; return res.end('{}'); }
  const request = JSON.parse(body.messages.at(-1).content);
  assert.equal(body.enable_thinking, false); assert.ok(!('response_format' in body));
  return res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { content: JSON.stringify(request.outputExample) } }] }));
});
await new Promise(resolve => server.listen(11450, '127.0.0.1', resolve));
try {
  const original = await call('/providers');
  for (const id of ['lemonade', 'deepseek']) {
    const found = await call(`/providers/${id}/models`);
    assert.equal(found.supported, true); assert.equal(found.error, null);
    assert.equal(found.models.length, 3);
    if (id === 'lemonade') assert.equal(found.models.find(m => m.id === 'image-only').chatCapable, false);
    await call(`/providers/${id}/models`);
  }
  assert.equal(calls.length, 2); assert.equal(calls.filter(c => c.authorization === 'Bearer local-smoke-token').length, 1);
  assert.deepEqual((await call('/providers')).profiles.map(p => [p.id, p.model, p.enabled]), original.profiles.map(p => [p.id, p.model, p.enabled]));
  await call('/providers/lemonade', { enabled: true, model: 'small-chat' }, 'PUT', 204);
  await call('/provider-routing', { narration: 'lemonade', reconstruction: 'lemonade', memory: 'lemonade', narrationModel: 'large-chat', reconstructionModel: 'large-chat', memoryModel: 'small-chat' }, 'PUT', 204);
  const routed = await call('/providers');
  assert.deepEqual(routed.taskModels, { narration: 'large-chat', reconstruction: 'large-chat', memory: 'small-chat' });
  const branch = await call('/campaigns', { name: 'Multiple model discovery verification', synthetic: true });
  await call(`/branches/${branch.id}/engine/settings`, { expectedCheckpointId: branch.headCheckpointId,
    settings: { characterProfile: 'lemonade', directorProfile: 'lemonade', characterModel: 'small-chat', directorModel: 'large-chat', maxBeats: 3, maxNpcs: 6 } }, 'POST', 204);
  let view = await call(`/branches/${branch.id}`);
  await call(`/branches/${branch.id}/turns`, { expectedCheckpointId: view.checkpoint.id, action: 'Ask Lyra about registration.' });
  view = await call(`/branches/${branch.id}`);
  assert.equal(view.runs[0].status, 'completed'); assert.equal(view.runs[0].model, 'large-chat');
  const inference = calls.filter(c => c.method === 'POST'); assert.ok(inference.length > 0);
  for (const c of inference) assert.equal(c.body.model, JSON.parse(c.body.messages.at(-1).content).task === 'character' ? 'small-chat' : 'large-chat');
  catalog = [{ id: 'new-model', downloaded: true }];
  assert.equal((await call('/providers/lemonade/models?refresh=true')).models[0].id, 'new-model');
  const saved = await call('/providers'); assert.equal(saved.profiles.find(p => p.id === 'lemonade').model, 'small-chat');
  assert.deepEqual(saved.taskModels, routed.taskModels);
  const world = await call(`/branches/${branch.id}/engine`);
  assert.equal(world.world.settings.characterModel, 'small-chat'); assert.equal(world.world.settings.directorModel, 'large-chat');
  console.log('PASS: HTTP discovery, caching/refresh, unchanged defaults, modality filtering, task model persistence and distinct character/director models through one Lemonade connector. Scripted inference only.');
} finally { await new Promise(resolve => server.close(resolve)); }

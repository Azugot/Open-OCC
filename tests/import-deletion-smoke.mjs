// Disposable PostgreSQL backend only; LEMONADE_BASE_URL=http://127.0.0.1:11450/v1.
// Scripted inference validates the complete HTTP workflow, not source interpretation.
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
const base = process.argv[2] || 'http://127.0.0.1:5080';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use a disposable local backend.');
const headers = {'X-Open-OCC':'1'};
async function call(path, body, method = body === undefined ? 'GET' : 'POST', status = 200) {
  const response = await fetch(base + '/api' + path, {method, headers: {...headers, ...(body instanceof FormData ? {} : {'Content-Type':'application/json'})}, body: body === undefined ? undefined : body instanceof FormData ? body : JSON.stringify(body)});
  const text = await response.text(); assert.equal(response.status, status, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
let completions = 0;
const server = createServer(async (req, res) => {
  const chunks = []; for await (const chunk of req) chunks.push(chunk);
  const body = JSON.parse(Buffer.concat(chunks).toString());
  const input = body.messages.at(-1).content;
  assert.ok(body.messages[0].content.length + input.length <= 24000);
  const evidence = JSON.parse(input.split('EVIDENCE:\n')[1].split('\nRELATED LEDGER:')[0]);
  const cite = [evidence.at(-1).ordinal];
  const state = {location:'Fixture harbor', time:'Noon', objective:'Fixture crossing', condition:'Rested', skills:{}, inventory:{Rations:2}, participants:['Lyra']};
  const scene = {state, evidenceOrdinals:cite, latestAction:'Prepare for the fixture crossing.', replyPending:true, missing:[],playerCharacterName:'Mara'};
  const claim = (key, category, text, subject, amount=0) => ({key, category, text, subject, target:'', value:'', amount, kind:'fact', visibility:'public', knownBy:[], confidence:.9, evidenceOrdinals:cite, disposition:'current', supersedesKeys:[]});
  const readOnly = input.startsWith('STAGE: reconcile') || input.startsWith('STAGE: audit');
  const result = {claims:readOnly || input.startsWith('STAGE: resume') ? [] : [claim('character:mara','character','Fixture navigator.','Mara'),claim('character:lyra','character','Fixture companion.','Lyra'),claim('inventory:rations','inventory','Two fixture rations.','Rations',2)], issues:[], resume:readOnly ? null : scene, summary:'Fixture extraction.\nFixture review.', resolvedIssueCodes:[]};
  // Reproduce literal newlines in a JSON string from the reported error.
  const malformed = JSON.stringify(result).replace('Fixture extraction.\\nFixture review.', 'Fixture extraction.\nFixture review.');
  completions++;
  res.setHeader('Content-Type','application/json'); res.end(JSON.stringify({choices:[{message:{content:malformed}}],usage:{prompt_tokens:10,completion_tokens:20}}));
});
await new Promise(resolve => server.listen(11450,'127.0.0.1',resolve));
try {
  await call('/providers/lemonade',{enabled:true,model:'scripted-import'},'PUT',204);
  await call('/provider-routing',{narration:'fixture',reconstruction:'fixture',memory:'fixture'},'PUT',204);
  const keep = await call('/campaigns',{name:'Disposable story to preserve',synthetic:true});
  const branch = await call('/campaigns',{name:'Disposable import and deletion',synthetic:false});
  const source = await readFile(new URL('../Story transcript.docx',import.meta.url));
  const form = new FormData(); form.append('file',new Blob([source]),'Story transcript.docx');
  form.append('inputCharacterLimit','24000'); form.append('outputTokenLimit','3000'); form.append('maxCalls','100');
  form.append('provider','lemonade'); form.append('model','scripted-import');
  form.append('prepareOnly','true');
  const job = await call(`/branches/${branch.id}/imports`,form,'POST',202);
  assert.equal(job.provider,'lemonade'); assert.equal(job.model,'scripted-import');
  assert.equal((await call('/providers')).tasks.reconstruction,'fixture');
  assert.equal(job.status,'prepared'); assert.equal(completions,0); assert.equal(job.calls,0);
  const structure = await call(`/sources/${job.sourceId}/structure`);
  assert.equal(structure.version,'document-v3'); assert.ok(structure.blockCount < 40);
  const blocks = await call(`/sources/${job.sourceId}/blocks?jobId=${job.id}`);
  assert.ok(blocks.items[0].text.includes('2. Gender:')); assert.ok(blocks.items[0].text.includes('Male / Female'));
  const searched = await call(`/sources/${job.sourceId}/blocks?jobId=${job.id}&q=Azugot`); assert.ok(searched.total > 0);
  await call(`/imports/${job.id}/resume-processing`,{maxCalls:100});
  let draft;
  for(let i=0;i<100;i++) {
    draft = await call(`/imports/${job.id}/draft`);
    if(!['queued','processing'].includes(draft.job.status)) break;
    await new Promise(resolve=>setTimeout(resolve,500));
  }
  assert.equal(draft.job.status,'review',draft.job.error);
  assert.equal(draft.job.processed,draft.job.total); assert.ok(draft.job.total < 40);
  const original = await fetch(`${base}/api/sources/${draft.source.id}/download`);
  assert.deepEqual(Buffer.from(await original.arrayBuffer()),source);
  await call(`/imports/${job.id}/approval`,{expectedCheckpointId:branch.headCheckpointId,revision:draft.job.proposalRevision},'POST',204);
  const view = await call(`/branches/${branch.id}`);
  assert.equal(view.state.inventory.Rations,2); assert.equal(view.messages.filter(m=>m.provider==='imported-source').length,0);
  const histories = await call(`/branches/${branch.id}/transcripts?checkpointId=${view.checkpoint.id}`); assert.equal(histories.total,1);
  const engine = await call(`/branches/${branch.id}/engine`);
  assert.ok(engine.world.events.length > 0); assert.ok(engine.world.memories.length > 0); assert.ok(engine.world.connections.length > 0);
  await call(`/branches/${branch.id}/engine/settings`,{expectedCheckpointId:view.checkpoint.id,settings:{characterProfile:'fixture',directorProfile:'fixture',maxBeats:3,maxNpcs:6}},'POST',204);
  for(const action of ['Inspect the supplies.','Wait one minute.']) {
    const before = await call(`/branches/${branch.id}`);
    await call(`/branches/${branch.id}/turns`,{expectedCheckpointId:before.checkpoint.id,action});
    const after = await call(`/branches/${branch.id}`);
    assert.notEqual(after.checkpoint.id,before.checkpoint.id);
    assert.ok(after.messages.some(m=>m.role==='assistant' && m.sequence > before.checkpoint.sequence && m.provider==='fixture' && m.content.trim()));
  }
  const exported = await call(`/campaigns/${branch.campaignId}/export`); assert.equal(exported.version,4);
  const restored = await call('/campaigns/import',{export:exported,name:'Disposable portable import'});
  assert.equal((await call(`/branches/${restored.id}/transcripts`)).total,1);
  const restoredEngine = await call(`/branches/${restored.id}/engine`);
  const restoredEvidence = restoredEngine.world.events.flatMap(e=>e.sourceEvidence||[]);
  assert.ok(restoredEvidence.length > 0);
  assert.ok(restoredEvidence.every(e=>e.sourceId !== job.sourceId));
  await call(`/campaigns/${restored.campaignId}`,undefined,'DELETE',204);
  await call(`/campaigns/${branch.campaignId}`,undefined,'DELETE',204);
  const campaigns = await call('/campaigns'); assert.ok(campaigns.some(c=>c.id===keep.campaignId)); assert.ok(!campaigns.some(c=>c.id===branch.campaignId));
  await call(`/imports/${job.id}/draft`,undefined,'GET',404);
  await call(`/campaigns/${keep.campaignId}`,undefined,'DELETE',204);
  console.log(`PASS: source-first grouped DOCX (${structure.paragraphCount} paragraphs -> ${structure.blockCount} blocks), range evidence, approval into Live world, two continuations, portable source remapping, PostgreSQL deletion and isolation (${completions} scripted calls).`);
} finally {await new Promise(resolve=>server.close(resolve));}

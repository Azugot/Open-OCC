// Recorded synthetic reconstruction responses. This server does not run an AI model.
import { createServer } from 'node:http';
const state = { location: 'Harbor', time: 'Day 7 · noon', objective: 'Cross the bay', condition: 'Rested', skills: { Navigation: 'F · 2/5' }, inventory: { Rations: 2 }, participants: ['Mara', 'Lyra'] };
function result(input) {
  const evidence = JSON.parse(input.split('EVIDENCE:\n')[1].split('\nRELATED LEDGER:')[0]);
  const cite = evidence.slice(0, 1).map(x => x.ordinal);
  const resume = { state, evidenceOrdinals: cite, latestAction: 'Pack two rations', replyPending: true, missing: [], playerCharacterName: "Mara" };
  const base = { claims: [], issues: [], resume: null, summary: 'Mara and Lyra prepare to cross the bay at noon.', resolvedIssueCodes: [] };
  if (input.startsWith('STAGE: audit') || input.startsWith('STAGE: reconcile')) return base;
  if (input.startsWith('STAGE: resume')) return { ...base, resume };
  const claim = (key, category, text, subject = '', target = '', value = '', amount = 0, kind = 'fact', visibility = 'public', knownBy = []) => ({ key, category, text, subject, target, value, amount, kind, visibility, knownBy, confidence: .9, evidenceOrdinals: cite, disposition: 'current', supersedesKeys: [] });
  return { ...base, resume, claims: [
    claim('character:mara', 'character', 'Mara is a navigator.', 'Mara'), claim('character:lyra', 'character', "Lyra is Mara's companion.", 'Lyra'),
    claim('inventory:rations', 'inventory', 'Two rations remain.', 'Rations', '', '', 2), claim('skill:navigation', 'skill', 'Navigation has two pips.', 'Navigation', '', 'F · 2/5'),
    claim('relationship:mara-lyra', 'relationship', 'Mara trusts Lyra.', 'Mara', 'Lyra', 'friend', 20),
    claim('fact:key', 'fact', 'The silver key opens the archive.', '', '', '', 0, 'secret', 'narrator', ['Mara']),
    claim('knowledge:mara-key', 'knowledge', 'Mara knows what the key opens.', 'Mara', 'fact:key', '', 0, 'secret', 'narrator', ['Mara']),
    claim('world:tides', 'world', 'Cross the bay at low tide.', 'rule', '', 'Tides'),
    claim('thread:crossing', 'thread', 'Cross before dusk.', 'Cross the bay', 'active', 'deadline', 5),
    claim('event:preparation', 'event', 'Mara packed her supplies.') ] };
}
createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && req.url?.endsWith('/models')) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ data: [{ id: 'scripted' }, { id: 'scripted-invalid' }, { id: 'scripted-entity-repair' }, { id: 'scripted-skill-repair' }] })); return;
    }
    let data = ''; for await (const chunk of req) { data += chunk; if (data.length > 1_000_000) throw Error('Request too large'); }
    const body = JSON.parse(data || '{}');
    const input = body.messages.at(-1).content;
    let output;
    if (body.model !== 'scripted-invalid') {
      output = result(input);
      if (body.model === 'scripted-entity-repair' && input.startsWith('STAGE: extract')) {
        const repairing = input.includes('VALIDATION REPAIR:');
        if (repairing && (!input.includes('claims[10] (entity:harbor)') || !input.includes('subject') || !input.includes('entityKind') || !input.includes('REJECTED RESPONSE:') || !input.includes('"entityKind":null')))
          throw Error('Entity repair did not receive exact errors and rejected data.');
        output.claims.push({ ...output.claims[0], key: 'entity:harbor', category: 'entity', text: 'Harbor is the departure point.', subject: repairing ? 'Harbor' : '', entityKind: repairing ? 'place' : null });
      }
      if (body.model === 'scripted-skill-repair' && input.startsWith('STAGE: extract')) {
        const repairing = input.includes('VALIDATION REPAIR:');
        if (repairing && (!input.includes("$.resume.state.skills['Magic Talent']") || !input.includes('plain string') || !input.includes('received object') || !input.includes('REJECTED RESPONSE:') || !input.includes('"rank":"F"')))
          throw Error('Skill repair did not receive the expected type and exact rejected value.');
        output.resume.state = { ...state, skills: { ...state.skills, 'Magic Talent': repairing ? 'F · 2/5' : { rank: 'F', progress: '2/5' } } };
        output.claims.push({ ...output.claims[3], key: 'skill:magic-talent', text: 'Magic Talent is F · 2/5.', subject: 'Magic Talent' });
      }
      else if (body.model === 'scripted-skill-repair' && output.resume)
        output.resume.state = { ...state, skills: { ...state.skills, 'Magic Talent': 'F · 2/5' } };
    }
    const content = body.model === 'scripted-invalid' ? '{truncated' : JSON.stringify(output);
    res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ choices: [{ message: { content } }], usage: { prompt_tokens: 10, completion_tokens: 20 } }));
  } catch (e) { res.writeHead(400); res.end(JSON.stringify({ error: e.message })); }
}).listen(23306, '0.0.0.0', () => console.log('Synthetic recorded model listening on port 23306.'));

// Protocol fixture: returns recorded output examples; never contacts an AI provider.
import { createServer } from 'node:http';
import { setTimeout as wait } from 'node:timers/promises';
let delay = 1200, fail = '', calls = [];
createServer(async (req, res) => {
  try {
    let raw = ''; for await (const part of req) raw += part;
    const body = JSON.parse(raw || '{}');
    if (req.url === '/control') { delay = body.delay ?? delay; fail = body.fail ?? ''; calls = []; res.end('{}'); return; }
    if (req.url === '/calls') { res.end(JSON.stringify(calls)); return; }
    if (req.url.endsWith('/models')) { res.end(JSON.stringify({ data: [{ id: 'recorded' }] })); return; }
    const input = body.messages.at(-1).content;
    let task; try { task = JSON.parse(input); } catch { task = null; }
    const stage = task?.task || (body.stream ? 'narrating' : 'checking-continuity');
    calls.push(stage); await wait(delay);
    if (stage === fail) { res.writeHead(503); res.end('Recorded failure'); return; }
    if (body.stream) {
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      for (const text of ['The harbor ', 'settles into silence. ', 'Your next choice awaits.']) {
        res.write(`data: ${JSON.stringify({ choices: [{ delta: { content: text } }] })}\n\n`);
        await wait(Math.min(delay, 600));
      }
      res.end('data: [DONE]\n\n');
    } else {
      res.setHeader('Content-Type', 'application/json');
      res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { content: JSON.stringify(task?.outputExample || { state: null, facts: [], threads: [] }) } }] }));
    }
  } catch (e) { res.writeHead(500); res.end(e.message); }
}).listen(23307, '0.0.0.0');

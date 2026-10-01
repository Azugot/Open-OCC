import { useEffect, useRef, useState } from "react";
import { api, downloadSource } from "./api";

export type Transcript = { sourceId: string; jobId: string; fileName: string; effectiveSequence: number; blockCount: number };
export type SourceEvidence = { sourceId: string; jobId: string; segmentId: string; ordinal: number; startOffset: number; endOffset: number };
type Block = { ordinal: number; text: string; speaker: string; section: string; start?: number; length?: number };
type Blocks = { source: { id: string; fileName: string }; total: number; pageSize?: number; items: Block[] };
export type Structure = { version: string; blockCount: number; paragraphCount: number; shortParagraphCount: number; characterCount: number };

export function TranscriptHistory({ branchId, checkpointId }: { branchId: string; checkpointId: string }) {
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<Transcript[]>([]);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<Transcript | null>(null);
  const [page, setPage] = useState(0); const [total, setTotal] = useState(0);
  useEffect(() => { setOpen(false); setRows([]); setSelected(null); setPage(0); setTotal(0); setError(""); }, [branchId, checkpointId]);
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController(); setError(""); setLoading(true);
    void api<{ items: Transcript[]; total: number }>(`/branches/${branchId}/transcripts?checkpointId=${checkpointId}&page=${page}`, { signal: controller.signal })
      .then(x => { setRows(x.items); setTotal(x.total); setLoading(false); }).catch(e => { if (e.name !== "AbortError") { setError(e.message); setLoading(false); } });
    return () => controller.abort();
  }, [open, branchId, checkpointId, page]);
  return <details className="panel imported-history" open={open} onToggle={e => setOpen(e.currentTarget.open)}>
    <summary>Preserved source documents</summary>
    <p>Read the original transcript separately from your continuing story. Source citations open their supporting text during import review.</p>
    {error && <p role="alert" className="text-error">{error}</p>}
    {open && loading && <p role="status">Loading source documents…</p>}
    {open && !loading && !error && total === 0 && <p>No approved source documents at this checkpoint.</p>}
    {rows.map(row => <div className="transcript-row" key={row.jobId}><div><strong>{row.fileName}</strong><small>{row.blockCount} source blocks · imported at checkpoint {row.effectiveSequence}</small></div><button onClick={() => setSelected(row)}>Read transcript</button></div>)}
    {total > 20 && <div className="transcript-pagination"><button disabled={page === 0} onClick={() => setPage(x => x - 1)}>Previous documents</button><span>Page {page + 1}</span><button disabled={(page + 1) * 20 >= total} onClick={() => setPage(x => x + 1)}>Next documents</button></div>}
    {selected && <TranscriptDrawer sourceId={selected.sourceId} jobId={selected.jobId} close={() => setSelected(null)} />}
  </details>;
}

export function StructurePreview({ sourceId, jobId }: { sourceId: string; jobId: string }) {
  const [data, setData] = useState<Structure | null>(null); const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController(); setData(null); setError("");
    void api<Structure>(`/sources/${sourceId}/structure?jobId=${jobId}`, { signal: controller.signal }).then(setData).catch(e => { if (e.name !== "AbortError") setError(e.message); });
    return () => controller.abort();
  }, [sourceId, jobId]);
  return <div className="source-structure"><h3>Document structure</h3>{error ? <p className="text-error">{error}</p> : data ? <><p>{data.characterCount.toLocaleString()} characters · {data.paragraphCount.toLocaleString()} source paragraphs grouped into <strong>{data.blockCount.toLocaleString()} blocks</strong></p><p>Headings, lists, and nearby text stay together. Exact source text is preserved; block boundaries provide reading context.</p></> : <p role="status">Reading document structure…</p>}</div>;
}

export function TranscriptDrawer({ sourceId, jobId, citations, close }: { sourceId: string; jobId: string; citations?: SourceEvidence[]; close: () => void }) {
  const dialog = useRef<HTMLElement>(null);
  const [page, setPage] = useState(0); const [query, setQuery] = useState(""); const [search, setSearch] = useState("");
  const [data, setData] = useState<Blocks | null>(null); const [error, setError] = useState(""); const [loading, setLoading] = useState(true);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null; dialog.current?.querySelector<HTMLButtonElement>("button")?.focus();
    return () => { if (previous?.isConnected) previous.focus(); };
  }, []);
  useEffect(() => { const timer = setTimeout(() => { setSearch(query); setPage(0); }, 250); return () => clearTimeout(timer); }, [query]);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(""); setData(null);
    const ordinals = citations ? `&ordinals=${[...new Set(citations.map(c => c.ordinal))].join(",")}` : "";
    void api<Blocks>(`/sources/${sourceId}/blocks?jobId=${jobId}&page=${page}&q=${encodeURIComponent(search)}${ordinals}`, { signal: controller.signal })
      .then(x => { setData(x); setLoading(false); }).catch(e => { if (e.name !== "AbortError") { setError(e.message); setLoading(false); } });
    return () => controller.abort();
  }, [sourceId, jobId, page, search, citations]);
  return <div className="evidence-overlay"><section ref={dialog} className="evidence-drawer transcript-drawer" role="dialog" aria-modal="true" aria-labelledby="transcript-title" onKeyDown={e => {
    if (e.key === "Escape") { e.preventDefault(); close(); }
    if (e.key === "Tab") {
      const items = Array.from(dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), a[href], [tabindex="0"]') ?? []);
      const first = items[0], last = items.at(-1);
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
    }
  }}>
    <button onClick={close}>Close transcript</button><h2 id="transcript-title">{data?.source.fileName || "Preserved transcript"}</h2>
    <p>{citations ? "Original source supporting this occurrence. Highlighted text marks the cited document range." : "Original source text. Speaker labels are shown only when the document identifies them."}</p>
    {data && <button onClick={() => void downloadSource(sourceId, data.source.fileName).catch(e => setError(e.message))}>Download original</button>}
    <label>Search transcript<input value={query} onChange={e => setQuery(e.target.value)} placeholder="A name, phrase, or place" /></label>
    {error && <p role="alert" className="text-error">{error}</p>}{loading && <p role="status">Loading source blocks…</p>}
    {data && <p role="status">{data.total.toLocaleString()} matching blocks · page {page + 1}</p>}
    {data?.items.map(block => <article className="source-block" key={block.ordinal}><small>Block {block.ordinal + 1}{block.speaker && block.speaker !== "source" ? ` · ${block.speaker}` : ""}{block.section ? ` · ${block.section}` : ""}{citations && (block.start ?? -1) >= 0 ? ` · document characters ${block.start! + 1}–${block.start! + block.text.length}` : ""}</small><div className="prose"><SourceText block={block} citations={citations} /></div></article>)}
    {data?.total === 0 && <p>No matching source text.</p>}
    <div className="transcript-pagination"><button disabled={loading || page === 0} onClick={() => setPage(x => x - 1)}>Previous blocks</button><button disabled={loading || !data || (page + 1) * (data.pageSize || 20) >= data.total} onClick={() => setPage(x => x + 1)}>Next blocks</button></div>
  </section></div>;
}

function SourceText({ block, citations }: { block: Block; citations?: SourceEvidence[] }) {
  if (!citations || (block.start ?? -1) < 0) return <>{block.text}</>;
  const ranges = citations.filter(c => c.ordinal === block.ordinal && c.startOffset >= block.start! && c.endOffset <= block.start! + block.text.length && c.endOffset > c.startOffset)
    .map(c => ({ start: c.startOffset - block.start!, end: c.endOffset - block.start! })).sort((a, b) => a.start - b.start);
  const merged: { start: number; end: number }[] = [];
  for (const range of ranges) {
    const last = merged.at(-1);
    if (last && range.start <= last.end) last.end = Math.max(last.end, range.end);
    else merged.push({ ...range });
  }
  const parts = []; let position = 0;
  for (const range of merged) { parts.push(block.text.slice(position, range.start), <mark key={range.start}>{block.text.slice(range.start, range.end)}</mark>); position = range.end; }
  parts.push(block.text.slice(position));
  return <>{parts}</>;
}

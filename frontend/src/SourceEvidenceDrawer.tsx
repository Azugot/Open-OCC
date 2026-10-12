import { useEffect, useRef, useState } from "react";
import { api } from "./api";
type EvidencePage = { total: number; items: { id: string; ordinal: number; speaker: string; text: string; section: string; artifact: boolean; timestamp?: string }[] };
export default function EvidenceDrawer({ id, ordinals, proposalId, grouped, close }: { id: string; ordinals: number[] | null; proposalId?: string; grouped: boolean; close: () => void }) {
  const dialog = useRef<HTMLElement>(null);
  const [page, setPage] = useState(0); const [data, setData] = useState<EvidencePage | null>(null); const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  useEffect(() => { const previous = document.activeElement as HTMLElement | null; dialog.current?.querySelector<HTMLButtonElement>("button")?.focus(); return () => { if (previous?.isConnected) previous.focus(); }; }, []);
  useEffect(() => {
    setData(null); setError("");
    if (!proposalId && ordinals?.length === 0) { setData({ total: 0, items: [] }); setLoading(false); return; }
    const controller = new AbortController(); setLoading(true);
    void api<EvidencePage>(`/imports/${id}/evidence?page=${page}${proposalId ? `&proposalId=${proposalId}` : ordinals ? `&ordinals=${ordinals.join(",")}` : ""}`, { signal: controller.signal })
      .then(x => { if (!controller.signal.aborted) setData(x); })
      .catch(e => { if (!controller.signal.aborted) setError(e.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [id, page, ordinals, proposalId]);
  return <div className="evidence-overlay"><section ref={dialog} className="evidence-drawer" role="dialog" aria-modal="true" aria-label="Source evidence" onKeyDown={e => {
    if (e.key === "Escape") { e.preventDefault(); close(); }
    if (e.key === "Tab") {
      const items = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
      if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items.at(-1)?.focus(); }
      else if (!e.shiftKey && document.activeElement === items.at(-1)) { e.preventDefault(); items[0]?.focus(); }
    }
  }}><button onClick={close}>Close evidence</button><h2>Preserved source {grouped ? "blocks" : "passages"}</h2>{loading && <p role="status">Loading evidence…</p>}{error && <p role="alert" className="text-error">{error}</p>}{data?.items.map(s => <blockquote key={s.id}><small>{grouped ? "Block" : "Passage"} {s.ordinal + 1}{s.speaker !== "source" ? ` · ${s.speaker}` : ""} · {s.section}{s.timestamp ? ` · ${s.timestamp}` : ""}{s.artifact ? " · formatting artifact" : ""}</small><p className="prose">{s.text}</p></blockquote>)}{data?.total === 0 && <p>No evidence cited.</p>}<button disabled={loading || page === 0} onClick={() => setPage(page - 1)}>Previous {grouped ? "blocks" : "passages"}</button>{" "}<button disabled={loading || !data || (page + 1) * 20 >= data.total} onClick={() => setPage(page + 1)}>Next {grouped ? "blocks" : "passages"}</button></section></div>;
}

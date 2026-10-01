import { useCallback, useEffect, useRef, useState } from "react";
import { api, post, downloadSource } from "./api";
import type { Job, Profiles, State } from "./api";
import ModelPicker from "./ModelPicker";
import { StructurePreview, TranscriptDrawer } from "./TranscriptViewer";

type Claim = { key: string; category: string; text: string; subject: string; target: string; value: string; amount: number; kind: string; visibility: string; knownBy: string[]; confidence: number; evidenceOrdinals: number[]; disposition: string; supersedesKeys: string[]; entityKind?: string | null; location?: string | null; actor?: string | null; relatedEntityKeys?: string[] | null; evidenceRanges?: unknown[] | null };
type Proposal = { proposal: { id: string; revision: number; current: boolean; excluded: boolean }; claim: Claim };
type Resume = { state: State | null; evidenceOrdinals: number[]; latestAction: string; replyPending: boolean; missing: string[]; playerCharacterName?: string | null };
type Issue = { id: string; code: string; text: string; blocking: boolean; evidenceJson: string; resolution: string };
type Draft = { job: Job; source: { id: string; fileName: string; sha256: string }; resume: Resume | null; issues: Issue[]; groups: { category: string; count: number }[]; sections: number };
type EvidencePage = { total: number; items: { id: string; ordinal: number; speaker: string; text: string; section: string; artifact: boolean; timestamp?: string }[] };
const categories = ["fact", "character", "relationship", "knowledge", "world", "event", "inventory", "skill", "condition", "thread", "entity", "connection"];

export default function ImportReview({ id, expectedCheckpointId, onApproved, onReanalyzed }: { id: string; expectedCheckpointId: string; onApproved: () => Promise<void>; onReanalyzed: (id: string) => Promise<void> }) {
  const [draft, setDraft] = useState<Draft | null>(null);
  const [rows, setRows] = useState<{ total: number; items: Proposal[] }>({ total: 0, items: [] });
  const [category, setCategory] = useState(""); const [query, setQuery] = useState(""); const [page, setPage] = useState(0); const [history, setHistory] = useState(false);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [notice, setNotice] = useState("");
  const [evidence, setEvidence] = useState<number[] | null | undefined>();
  const [browse, setBrowse] = useState(false);
  const [profiles, setProfiles] = useState<Profiles | null>(null); const [provider, setProvider] = useState(""); const [model, setModel] = useState("");
  const [maxCalls, setMaxCalls] = useState(100); const [inputLimit, setInputLimit] = useState(24000); const [outputLimit, setOutputLimit] = useState(3000);
  const load = useCallback(async () => {
    const data = await api<Draft>(`/imports/${id}/draft`); setDraft(data);
  }, [id]);
  const loadRows = useCallback(async () => setRows(await api(`/imports/${id}/proposals?page=${page}&category=${encodeURIComponent(category)}&q=${encodeURIComponent(query)}&history=${history}`)), [id, page, category, query, history]);
  useEffect(() => { void load().catch(e => setError(e.message)); void api<Profiles>("/providers").then(setProfiles).catch(e => setError(e.message)); }, [load]);
  useEffect(() => { const timer = setTimeout(() => void loadRows().catch(e => setError(e.message)), 200); return () => clearTimeout(timer); }, [loadRows, draft?.job.proposalRevision]);
  useEffect(() => {
    if (!draft || !["queued", "processing"].includes(draft.job.status)) return;
    const timer = setInterval(() => void load().catch(e => setError(e.message)), 2000); return () => clearInterval(timer);
  }, [draft?.job.status, load]);
  useEffect(() => { if (draft) { setMaxCalls(draft.job.maxCalls || 100); setInputLimit(draft.job.inputCharacterLimit || 24000); setOutputLimit(draft.job.outputTokenLimit || 3000); } }, [draft?.job.id]);
  const perform = async (action: () => Promise<void>) => { setBusy(true); setError(""); setNotice(""); try { await action(); await load(); await loadRows(); setNotice("Saved."); } catch (e) { setError((e as Error).message); } finally { setBusy(false); } };
  if (!draft) return <p>{error || "Loading reconstruction…"}</p>;
  const editable = draft.job.status === "review" && draft.job.reviewCompleted;
  const grouped = draft.job.method === "agent-v3";
  const unresolved = draft.issues.filter(x => x.blocking && !x.resolution).length;
  return <div className="import-review">
    {error && <p role="alert" className="text-error">{error}</p>}{notice && <p role="status">{notice}</p>}
    <div className="panel">
      <span className="badge">{draft.job.status} · {draft.job.stage}</span><h2>{draft.source.fileName}</h2>
      <p>{draft.job.processed}/{draft.job.total || "?"} {grouped ? "blocks" : "passages"} analyzed · {draft.sections} reading sections · {draft.job.provider} / {draft.job.model}</p>
      <p>{grouped ? "The source stays intact. Models read consecutive blocks with nearby context and a running chronology; proposals retain links to supporting source text." : "This older import cites individual passages. Re-read the complete original to use grouped source blocks."}</p>
      {grouped && <StructurePreview sourceId={draft.source.id} jobId={id} />}
      <p>{draft.job.calls || 0}/{draft.job.maxCalls} model calls · Reported tokens: {draft.job.inputTokens ?? "unavailable"} input / {draft.job.outputTokens ?? "unavailable"} output</p>
      {draft.job.summary && <p>{draft.job.summary}</p>}{draft.job.error && <p className="text-error">{draft.job.error}</p>}
      <button onClick={() => void perform(async () => downloadSource(draft.source.id, draft.source.fileName))}>Download original</button>{" "}
      <button onClick={() => grouped ? setBrowse(true) : setEvidence(null)}>{grouped ? "Preview source blocks" : "Browse preserved passages"}</button>
      <small className="hash">SHA-256 · {draft.source.sha256}</small>
      {["queued", "processing"].includes(draft.job.status) && <button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/cancel`); })}>Cancel processing</button>}
      {["prepared", "paused", "cancelled", "failed"].includes(draft.job.status) && <div className="import-controls">
        <h3>{draft.job.status === "prepared" ? "Start reconstruction" : "Resume the saved stage"}</h3>
        {draft.job.status === "prepared" && <p>No model calls have been made. Inspect the grouped source, choose your model and budget, then start. Graphs, registered characters, memories, and Live world history are committed together when you approve the reconstruction.</p>}
        <label>Model profile<select value={provider} onChange={e => { setProvider(e.target.value); setModel(profiles?.profiles.find(p => p.id === e.target.value)?.model || ""); }}><option value="">Keep {draft.job.provider} / {draft.job.model}</option>{profiles?.profiles.filter(p => p.enabled && p.id !== "fixture").map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        {provider && <ModelPicker profile={profiles?.profiles.find(p => p.id === provider)} value={model} onChange={setModel} label="Reconstruction model" />}
        <label>Total call budget<input type="number" min={1} max={1000} value={maxCalls} onChange={e => setMaxCalls(Number(e.target.value))} /></label>
        <label>Input character budget<input type="number" min={12000} max={120000} value={inputLimit} onChange={e => setInputLimit(Number(e.target.value))} /></label>
        <label>Output token limit<input type="number" min={1000} max={12000} value={outputLimit} onChange={e => setOutputLimit(Number(e.target.value))} /></label>
        <button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/resume-processing`, { provider: provider || null, model: model || null, maxCalls, inputCharacterLimit: inputLimit, outputTokenLimit: outputLimit }); })}>{draft.job.status === "prepared" ? "Start reconstruction" : "Resume reconstruction"}</button>
      </div>}
      {!["prepared", "queued", "processing"].includes(draft.job.status) && <details className="import-controls"><summary>Re-read the complete original</summary>
        <p>Create a fresh reconstruction using grouped source blocks and connected reading sections. The current draft and approved story stay available.</p>
        <label>Input character budget<input type="number" min={12000} max={120000} value={inputLimit} onChange={e => setInputLimit(Number(e.target.value))} /></label>
        <label>Output token limit<input type="number" min={1000} max={12000} value={outputLimit} onChange={e => setOutputLimit(Number(e.target.value))} /></label>
        <label>Total call budget<input type="number" min={1} max={1000} value={maxCalls} onChange={e => setMaxCalls(Number(e.target.value))} /></label>
        <button disabled={busy} onClick={() => void perform(async () => {
          const job = await post<Job>(`/imports/${id}/reanalyze`, { inputCharacterLimit: inputLimit, outputTokenLimit: outputLimit, maxCalls });
          await onReanalyzed(job.id);
        })}>Start fresh reconstruction</button>
      </details>}
    </div>
    {draft.job.status !== "prepared" && <>
    <div className="panel"><h2>Review exceptions · {unresolved} unresolved</h2>
      <p>Resolve conflicts and acknowledge information the source cannot establish. Clear proposals are included unless you exclude them.</p>
      {!draft.issues.length && <p>{editable ? "No review exceptions were reported." : "Exceptions appear after analysis and review."}</p>}
      {draft.issues.map(issue => <IssueCard key={issue.id} issue={issue} disabled={!editable || busy} evidence={() => setEvidence(JSON.parse(issue.evidenceJson))} save={resolution => perform(async () => { await api(`/imports/${id}/issues/${issue.id}`, { method: "PUT", body: JSON.stringify({ revision: draft.job.proposalRevision, resolution }) }); })} />)}
    </div>
    <div className="panel"><h2>Campaign proposal</h2>
      <div className="import-filters"><label>Search<input value={query} onChange={e => { setQuery(e.target.value); setPage(0); }} placeholder="Names, claims, events…" /></label>
      <label>Category<select value={category} onChange={e => { setCategory(e.target.value); setPage(0); }}><option value="">All categories</option>{categories.map(c => <option key={c} value={c}>{c} ({draft.groups.find(g => g.category === c)?.count || 0})</option>)}</select></label>
      <label className="check-label"><input type="checkbox" checked={history} onChange={e => { setHistory(e.target.checked); setPage(0); }} />Show superseded versions</label></div>
      <p>{rows.total} proposals · page {page + 1}</p>
      {rows.items.map(row => <ProposalCard key={row.proposal.id} row={row} grouped={grouped} disabled={!editable || busy || !row.proposal.current} evidence={() => setEvidence(row.claim.evidenceOrdinals)} save={(claim, excluded) => perform(async () => { await api(`/imports/${id}/proposals/${row.proposal.id}`, { method: "PUT", body: JSON.stringify({ revision: draft.job.proposalRevision, claim, excluded }) }); })} />)}
      {!rows.items.length && <p>No proposals in this view.</p>}
      <button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous</button>{" "}<button disabled={(page + 1) * 20 >= rows.total} onClick={() => setPage(page + 1)}>Next</button>
    </div>
    <ResumeCard key={`${draft.job.id}-${draft.job.resumeJson}`} resume={draft.resume} grouped={grouped} disabled={!editable || busy} evidence={() => setEvidence(draft.resume?.evidenceOrdinals || [])} save={resume => perform(async () => { await api(`/imports/${id}/resume`, { method: "PUT", body: JSON.stringify({ revision: draft.job.proposalRevision, resume }) }); })} />
    {editable && <div className="panel"><p>Approval commits the included proposals, preserved source, resume checkpoint, registered characters, graph, memories, and Live world occurrences together.</p>
      {grouped && !draft.resume?.playerCharacterName?.trim() && <p className="text-error">Set and save the actual player character’s name in the resume checkpoint before approval.</p>}
      <button className="primary" disabled={busy || unresolved > 0 || !draft.resume?.state || (grouped && !draft.resume.playerCharacterName?.trim())} onClick={() => void perform(async () => { await post(`/imports/${id}/approval`, { expectedCheckpointId, revision: draft.job.proposalRevision }); await onApproved(); })}>Approve reconstruction</button></div>}
    </>}
    {evidence !== undefined && <EvidenceDrawer id={id} ordinals={evidence} grouped={grouped} close={() => setEvidence(undefined)} />}
    {browse && <TranscriptDrawer sourceId={draft.source.id} jobId={id} close={() => setBrowse(false)} />}
  </div>;
}
function IssueCard({ issue, disabled, evidence, save }: { issue: Issue; disabled: boolean; evidence: () => void; save: (resolution: string) => Promise<void> }) {
  const [resolution, setResolution] = useState(issue.resolution);
  return <div className="import-item"><strong>{issue.blocking ? "Requires review" : "Note"} · {issue.code}</strong><p>{issue.text}</p><button onClick={evidence}>Evidence</button>
    <label>Resolution or acknowledgment<textarea value={resolution} disabled={disabled} onChange={e => setResolution(e.target.value)} maxLength={2000} /></label><button disabled={disabled || !resolution.trim()} onClick={() => void save(resolution)}>Save resolution</button>{issue.resolution && <small>Saved: {issue.resolution}</small>}</div>;
}
function ProposalCard({ row, grouped, disabled, evidence, save }: { row: Proposal; grouped: boolean; disabled: boolean; evidence: () => void; save: (claim: Claim, excluded: boolean) => Promise<void> }) {
  const [claim, setClaim] = useState(row.claim); const [excluded, setExcluded] = useState(row.proposal.excluded);
  const change = (field: keyof Claim, value: unknown) => setClaim({ ...claim, [field]: value });
  return <article className="import-item"><span className="badge">{claim.category} · {claim.disposition}{!row.proposal.current ? " · superseded" : ""}{row.proposal.excluded ? " · excluded" : ""}</span><p>{claim.text}</p><small>Model confidence estimate {Math.round(claim.confidence * 100)}% · {claim.evidenceOrdinals.length} supporting {grouped ? "blocks" : "passages"}</small><button onClick={evidence}>Evidence</button>
    <details><summary>Edit proposal</summary><label>Statement<textarea disabled={disabled} value={claim.text} onChange={e => change("text", e.target.value)} maxLength={1000} /></label>
      <div className="import-filters"><label>Subject<input disabled={disabled} value={claim.subject} onChange={e => change("subject", e.target.value)} /></label><label>Target<input disabled={disabled} value={claim.target} onChange={e => change("target", e.target.value)} /></label><label>Value<input disabled={disabled} value={claim.value} onChange={e => change("value", e.target.value)} /></label><label>Amount / score<input disabled={disabled} type="number" value={claim.amount} onChange={e => change("amount", Number(e.target.value))} /></label></div>
      <label>Claim type<select disabled={disabled} value={claim.kind} onChange={e => change("kind", e.target.value)}>{["fact", "rumor", "belief", "secret", "correction"].map(k => <option key={k}>{k}</option>)}</select></label>
      <label>Visibility<select disabled={disabled} value={claim.visibility} onChange={e => change("visibility", e.target.value)}><option value="public">Public</option><option value="narrator">Narrator only</option></select></label>
      <label>Characters who know this<input disabled={disabled} value={claim.knownBy.join(", ")} onChange={e => change("knownBy", e.target.value.split(",").map(x => x.trim()).filter(Boolean))} /></label>
      <label>Current or historical<select disabled={disabled} value={claim.disposition} onChange={e => change("disposition", e.target.value)}><option value="current">Current</option><option value="historical">Historical</option></select></label>
      {grouped && <div className="import-filters"><label>Entity kind<select disabled={disabled} value={claim.entityKind || ""} onChange={e => change("entityKind", e.target.value || null)}><option value="">Unspecified</option>{["character", "object", "being", "place"].map(kind => <option key={kind} value={kind}>{kind}</option>)}</select></label><label>Location<input disabled={disabled} value={claim.location || ""} onChange={e => change("location", e.target.value || null)} /></label><label>Actor<input disabled={disabled} value={claim.actor || ""} onChange={e => change("actor", e.target.value || null)} /></label></div>}
      {grouped && <label>Related entity keys<input disabled={disabled} value={(claim.relatedEntityKeys || []).join(", ")} onChange={e => change("relatedEntityKeys", e.target.value.split(",").map(x => x.trim()).filter(Boolean))} /><small>Reference registered characters, objects, beings or places. Event and thread keys are not entities.</small></label>}
      <label className="check-label"><input disabled={disabled} type="checkbox" checked={excluded} onChange={e => setExcluded(e.target.checked)} />Exclude from approval</label>
      <button disabled={disabled} onClick={() => void save(claim, excluded)}>Save proposal</button></details></article>;
}
function ResumeCard({ resume, grouped, disabled, evidence, save }: { resume: Resume | null; grouped: boolean; disabled: boolean; evidence: () => void; save: (resume: Resume) => Promise<void> }) {
  const [state, setState] = useState<State>(resume?.state || { location: "", time: "Unknown", objective: "", condition: "Unknown", skills: {}, inventory: {}, participants: [] });
  const [inventory, setInventory] = useState(JSON.stringify(state.inventory, null, 2)); const [skills, setSkills] = useState(JSON.stringify(state.skills, null, 2));
  const [latestAction, setLatestAction] = useState(resume?.latestAction || ""); const [pending, setPending] = useState(resume?.replyPending || false); const [ordinals, setOrdinals] = useState((resume?.evidenceOrdinals || []).map(x => x + 1).join(", "));
  const [playerName, setPlayerName] = useState(resume?.playerCharacterName || "");
  const [advanced, setAdvanced] = useState(""); const [error, setError] = useState("");
  return <div className="panel"><h2>Resume checkpoint</h2>{!resume?.state && <p className="text-error">No final scene has been reconstructed. Supply the missing information after agent review; the empty campaign state cannot be approved.</p>}
    {error && <p className="text-error">{error}</p>}<button onClick={evidence}>Resume evidence</button>
    {(["location", "time", "objective", "condition"] as const).map(field => <label key={field}>{field}<input disabled={disabled} value={state[field]} onChange={e => setState({ ...state, [field]: e.target.value })} /></label>)}
    {grouped && <label>Player character’s actual name<input disabled={disabled} value={playerName} maxLength={200} onChange={e => setPlayerName(e.target.value)} /><small>This profile belongs to the player and is not registered as an autonomous NPC.</small></label>}
    <label>Characters physically present in this scene<input disabled={disabled} value={state.participants.join(", ")} onChange={e => setState({ ...state, participants: e.target.value.split(",").map(x => x.trim()).filter(Boolean) })} /><small>Include people who are here now, rather than everyone mentioned in the transcript.</small></label>
    <div className="import-filters"><label>Inventory quantities<textarea disabled={disabled} value={inventory} onChange={e => setInventory(e.target.value)} /></label><label>Skill ranks / progress<textarea disabled={disabled} value={skills} onChange={e => setSkills(e.target.value)} /></label></div>
    <label>Latest player action<textarea disabled={disabled} value={latestAction} onChange={e => setLatestAction(e.target.value)} /></label><label className="check-label"><input disabled={disabled} type="checkbox" checked={pending} onChange={e => setPending(e.target.checked)} />Narrator response is pending</label>
    <label>Supporting {grouped ? "block" : "passage"} numbers<input disabled={disabled} value={ordinals} onChange={e => setOrdinals(e.target.value)} /></label>
    <details><summary>Advanced state JSON</summary><textarea className="state-editor" disabled={disabled} placeholder="Optional complete state override" value={advanced} onChange={e => setAdvanced(e.target.value)} /></details>
    <button disabled={disabled} onClick={() => { try { const parsed = advanced.trim() ? JSON.parse(advanced) : { ...state, inventory: JSON.parse(inventory), skills: JSON.parse(skills) }; const evidenceOrdinals = ordinals.split(",").filter(x => x.trim()).map(x => Number(x.trim()) - 1); if (evidenceOrdinals.some(x => !Number.isInteger(x) || x < 0)) throw new Error(`${grouped ? "Block" : "Passage"} numbers must be positive integers.`); if (grouped && !playerName.trim()) throw new Error("Provide the actual player character’s name."); setError(""); void save({ ...resume, state: parsed, latestAction, replyPending: pending, evidenceOrdinals, missing: resume?.missing || [], playerCharacterName: playerName.trim() || resume?.playerCharacterName }); } catch (e) { setError((e as Error).message); } }}>Save resume checkpoint</button>
  </div>;
}
function EvidenceDrawer({ id, ordinals, grouped, close }: { id: string; ordinals: number[] | null; grouped: boolean; close: () => void }) {
  const dialog = useRef<HTMLElement>(null);
  const [page, setPage] = useState(0); const [data, setData] = useState<EvidencePage | null>(null); const [error, setError] = useState("");
  useEffect(() => { const previous = document.activeElement as HTMLElement | null; dialog.current?.querySelector<HTMLButtonElement>("button")?.focus(); return () => { if (previous?.isConnected) previous.focus(); }; }, []);
  useEffect(() => { if (ordinals?.length === 0) { setData({ total: 0, items: [] }); return; } let active = true; void api<EvidencePage>(`/imports/${id}/evidence?page=${page}${ordinals ? `&ordinals=${ordinals.join(",")}` : ""}`).then(x => { if (active) setData(x); }).catch(e => { if (active) setError(e.message); }); return () => { active = false; }; }, [id, page, ordinals]);
  return <div className="evidence-overlay"><section ref={dialog} className="evidence-drawer" role="dialog" aria-modal="true" aria-label="Source evidence" onKeyDown={e => {
    if (e.key === "Escape") { e.preventDefault(); close(); }
    if (e.key === "Tab") {
      const items = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
      if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items.at(-1)?.focus(); }
      else if (!e.shiftKey && document.activeElement === items.at(-1)) { e.preventDefault(); items[0]?.focus(); }
    }
  }}><button onClick={close}>Close evidence</button><h2>Preserved source {grouped ? "blocks" : "passages"}</h2>{error && <p className="text-error">{error}</p>}{data?.items.map(s => <blockquote key={s.id}><small>{grouped ? "Block" : "Passage"} {s.ordinal + 1}{s.speaker !== "source" ? ` · ${s.speaker}` : ""} · {s.section}{s.timestamp ? ` · ${s.timestamp}` : ""}{s.artifact ? " · formatting artifact" : ""}</small><p className="prose">{s.text}</p></blockquote>)}{data?.total === 0 && <p>No evidence cited.</p>}<button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous {grouped ? "blocks" : "passages"}</button>{" "}<button disabled={!data || (page + 1) * 20 >= data.total} onClick={() => setPage(page + 1)}>Next {grouped ? "blocks" : "passages"}</button></section></div>;
}

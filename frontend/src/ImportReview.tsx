import { useCallback, useEffect, useState } from "react";
import { api, post, downloadSource } from "./api";
import type { Job, Profiles, State } from "./api";

type Claim = { key: string; category: string; text: string; subject: string; target: string; value: string; amount: number; kind: string; visibility: string; knownBy: string[]; confidence: number; evidenceOrdinals: number[]; disposition: string; supersedesKeys: string[] };
type Proposal = { proposal: { id: string; revision: number; current: boolean; excluded: boolean }; claim: Claim };
type Resume = { state: State | null; evidenceOrdinals: number[]; latestAction: string; replyPending: boolean; missing: string[] };
type Issue = { id: string; code: string; text: string; blocking: boolean; evidenceJson: string; resolution: string };
type Draft = { job: Job; source: { id: string; fileName: string; sha256: string }; resume: Resume | null; issues: Issue[]; groups: { category: string; count: number }[]; sections: number };
type EvidencePage = { total: number; items: { id: string; ordinal: number; speaker: string; text: string; section: string; artifact: boolean; timestamp?: string }[] };
const categories = ["fact", "character", "relationship", "knowledge", "world", "event", "inventory", "skill", "condition", "thread"];

export default function ImportReview({ id, expectedCheckpointId, onApproved }: { id: string; expectedCheckpointId: string; onApproved: () => Promise<void> }) {
  const [draft, setDraft] = useState<Draft | null>(null);
  const [rows, setRows] = useState<{ total: number; items: Proposal[] }>({ total: 0, items: [] });
  const [category, setCategory] = useState(""); const [query, setQuery] = useState(""); const [page, setPage] = useState(0); const [history, setHistory] = useState(false);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const [notice, setNotice] = useState("");
  const [evidence, setEvidence] = useState<number[] | null | undefined>();
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
  const unresolved = draft.issues.filter(x => x.blocking && !x.resolution).length;
  return <div className="import-review">
    {error && <p role="alert" className="text-error">{error}</p>}{notice && <p role="status">{notice}</p>}
    <div className="panel">
      <span className="badge">{draft.job.status} · {draft.job.stage}</span><h2>{draft.source.fileName}</h2>
      <p>{draft.job.processed}/{draft.job.total || "?"} passages analyzed · {draft.sections} reading sections · {draft.job.provider} / {draft.job.model}</p>
      <p>{draft.job.calls || 0}/{draft.job.maxCalls} model calls · Reported tokens: {draft.job.inputTokens ?? "unavailable"} input / {draft.job.outputTokens ?? "unavailable"} output</p>
      {draft.job.summary && <p>{draft.job.summary}</p>}{draft.job.error && <p className="text-error">{draft.job.error}</p>}
      <button onClick={() => void perform(async () => downloadSource(draft.source.id, draft.source.fileName))}>Download original</button>{" "}
      <button onClick={() => setEvidence(null)}>Browse preserved passages</button>
      <small className="hash">SHA-256 · {draft.source.sha256}</small>
      {["queued", "processing"].includes(draft.job.status) && <button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/cancel`); })}>Cancel processing</button>}
      {["paused", "cancelled", "failed"].includes(draft.job.status) && <div className="import-controls">
        <h3>Resume the saved stage</h3>
        <label>Model profile<select value={provider} onChange={e => { setProvider(e.target.value); setModel(profiles?.profiles.find(p => p.id === e.target.value)?.model || ""); }}><option value="">Keep {draft.job.provider} / {draft.job.model}</option>{profiles?.profiles.filter(p => p.enabled && p.id !== "fixture").map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        {provider && <label>Model<input value={model} onChange={e => setModel(e.target.value)} /></label>}
        <label>Total call budget<input type="number" min={1} max={1000} value={maxCalls} onChange={e => setMaxCalls(Number(e.target.value))} /></label>
        <label>Input character budget<input type="number" min={12000} max={120000} value={inputLimit} onChange={e => setInputLimit(Number(e.target.value))} /></label>
        <label>Output token limit<input type="number" min={1000} max={12000} value={outputLimit} onChange={e => setOutputLimit(Number(e.target.value))} /></label>
        <button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/resume-processing`, { provider: provider || null, model: model || null, maxCalls, inputCharacterLimit: inputLimit, outputTokenLimit: outputLimit }); })}>Resume reconstruction</button>
      </div>}
    </div>
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
      {rows.items.map(row => <ProposalCard key={row.proposal.id} row={row} disabled={!editable || busy || !row.proposal.current} evidence={() => setEvidence(row.claim.evidenceOrdinals)} save={(claim, excluded) => perform(async () => { await api(`/imports/${id}/proposals/${row.proposal.id}`, { method: "PUT", body: JSON.stringify({ revision: draft.job.proposalRevision, claim, excluded }) }); })} />)}
      {!rows.items.length && <p>No proposals in this view.</p>}
      <button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous</button>{" "}<button disabled={(page + 1) * 20 >= rows.total} onClick={() => setPage(page + 1)}>Next</button>
    </div>
    <ResumeCard key={`${draft.job.id}-${draft.job.resumeJson}`} resume={draft.resume} disabled={!editable || busy} evidence={() => setEvidence(draft.resume?.evidenceOrdinals || [])} save={resume => perform(async () => { await api(`/imports/${id}/resume`, { method: "PUT", body: JSON.stringify({ revision: draft.job.proposalRevision, resume }) }); })} />
    {editable && <div className="panel"><p>Approval commits the included proposals, source history, and saved resume checkpoint together.</p>
      <button className="primary" disabled={busy || unresolved > 0 || !draft.resume?.state} onClick={() => void perform(async () => { await post(`/imports/${id}/approval`, { expectedCheckpointId, revision: draft.job.proposalRevision }); await onApproved(); })}>Approve reconstruction</button></div>}
    {evidence !== undefined && <EvidenceDrawer id={id} ordinals={evidence} close={() => setEvidence(undefined)} />}
  </div>;
}
function IssueCard({ issue, disabled, evidence, save }: { issue: Issue; disabled: boolean; evidence: () => void; save: (resolution: string) => Promise<void> }) {
  const [resolution, setResolution] = useState(issue.resolution);
  return <div className="import-item"><strong>{issue.blocking ? "Requires review" : "Note"} · {issue.code}</strong><p>{issue.text}</p><button onClick={evidence}>Evidence</button>
    <label>Resolution or acknowledgment<textarea value={resolution} disabled={disabled} onChange={e => setResolution(e.target.value)} maxLength={2000} /></label><button disabled={disabled || !resolution.trim()} onClick={() => void save(resolution)}>Save resolution</button>{issue.resolution && <small>Saved: {issue.resolution}</small>}</div>;
}
function ProposalCard({ row, disabled, evidence, save }: { row: Proposal; disabled: boolean; evidence: () => void; save: (claim: Claim, excluded: boolean) => Promise<void> }) {
  const [claim, setClaim] = useState(row.claim); const [excluded, setExcluded] = useState(row.proposal.excluded);
  const change = (field: keyof Claim, value: unknown) => setClaim({ ...claim, [field]: value });
  return <article className="import-item"><span className="badge">{claim.category} · {claim.disposition}{!row.proposal.current ? " · superseded" : ""}{row.proposal.excluded ? " · excluded" : ""}</span><p>{claim.text}</p><small>Model confidence estimate {Math.round(claim.confidence * 100)}% · {claim.evidenceOrdinals.length} supporting passages</small><button onClick={evidence}>Evidence</button>
    <details><summary>Edit proposal</summary><label>Statement<textarea disabled={disabled} value={claim.text} onChange={e => change("text", e.target.value)} maxLength={1000} /></label>
      <div className="import-filters"><label>Subject<input disabled={disabled} value={claim.subject} onChange={e => change("subject", e.target.value)} /></label><label>Target<input disabled={disabled} value={claim.target} onChange={e => change("target", e.target.value)} /></label><label>Value<input disabled={disabled} value={claim.value} onChange={e => change("value", e.target.value)} /></label><label>Amount / score<input disabled={disabled} type="number" value={claim.amount} onChange={e => change("amount", Number(e.target.value))} /></label></div>
      <label>Claim type<select disabled={disabled} value={claim.kind} onChange={e => change("kind", e.target.value)}>{["fact", "rumor", "belief", "secret", "correction"].map(k => <option key={k}>{k}</option>)}</select></label>
      <label>Visibility<select disabled={disabled} value={claim.visibility} onChange={e => change("visibility", e.target.value)}><option value="public">Public</option><option value="narrator">Narrator only</option></select></label>
      <label>Characters who know this<input disabled={disabled} value={claim.knownBy.join(", ")} onChange={e => change("knownBy", e.target.value.split(",").map(x => x.trim()).filter(Boolean))} /></label>
      <label>Current or historical<select disabled={disabled} value={claim.disposition} onChange={e => change("disposition", e.target.value)}><option value="current">Current</option><option value="historical">Historical</option></select></label>
      <label className="check-label"><input disabled={disabled} type="checkbox" checked={excluded} onChange={e => setExcluded(e.target.checked)} />Exclude from approval</label>
      <button disabled={disabled} onClick={() => void save(claim, excluded)}>Save proposal</button></details></article>;
}
function ResumeCard({ resume, disabled, evidence, save }: { resume: Resume | null; disabled: boolean; evidence: () => void; save: (resume: Resume) => Promise<void> }) {
  const [state, setState] = useState<State>(resume?.state || { location: "", time: "Unknown", objective: "", condition: "Unknown", skills: {}, inventory: {}, participants: [] });
  const [inventory, setInventory] = useState(JSON.stringify(state.inventory, null, 2)); const [skills, setSkills] = useState(JSON.stringify(state.skills, null, 2));
  const [latestAction, setLatestAction] = useState(resume?.latestAction || ""); const [pending, setPending] = useState(resume?.replyPending || false); const [ordinals, setOrdinals] = useState((resume?.evidenceOrdinals || []).map(x => x + 1).join(", "));
  const [advanced, setAdvanced] = useState(""); const [error, setError] = useState("");
  return <div className="panel"><h2>Resume checkpoint</h2>{!resume?.state && <p className="text-error">No final scene has been reconstructed. Supply the missing information after agent review; the empty campaign state cannot be approved.</p>}
    {error && <p className="text-error">{error}</p>}<button onClick={evidence}>Resume evidence</button>
    {(["location", "time", "objective", "condition"] as const).map(field => <label key={field}>{field}<input disabled={disabled} value={state[field]} onChange={e => setState({ ...state, [field]: e.target.value })} /></label>)}
    <label>Participants<input disabled={disabled} value={state.participants.join(", ")} onChange={e => setState({ ...state, participants: e.target.value.split(",").map(x => x.trim()).filter(Boolean) })} /></label>
    <div className="import-filters"><label>Inventory quantities<textarea disabled={disabled} value={inventory} onChange={e => setInventory(e.target.value)} /></label><label>Skill ranks / progress<textarea disabled={disabled} value={skills} onChange={e => setSkills(e.target.value)} /></label></div>
    <label>Latest player action<textarea disabled={disabled} value={latestAction} onChange={e => setLatestAction(e.target.value)} /></label><label className="check-label"><input disabled={disabled} type="checkbox" checked={pending} onChange={e => setPending(e.target.checked)} />Narrator response is pending</label>
    <label>Supporting passage numbers<input disabled={disabled} value={ordinals} onChange={e => setOrdinals(e.target.value)} /></label>
    <details><summary>Advanced state JSON</summary><textarea className="state-editor" disabled={disabled} placeholder="Optional complete state override" value={advanced} onChange={e => setAdvanced(e.target.value)} /></details>
    <button disabled={disabled} onClick={() => { try { const parsed = advanced.trim() ? JSON.parse(advanced) : { ...state, inventory: JSON.parse(inventory), skills: JSON.parse(skills) }; const evidenceOrdinals = ordinals.split(",").filter(x => x.trim()).map(x => Number(x.trim()) - 1); if (evidenceOrdinals.some(x => !Number.isInteger(x) || x < 0)) throw new Error("Passage numbers must be positive integers."); setError(""); void save({ state: parsed, latestAction, replyPending: pending, evidenceOrdinals, missing: resume?.missing || [] }); } catch (e) { setError((e as Error).message); } }}>Save resume checkpoint</button>
  </div>;
}
function EvidenceDrawer({ id, ordinals, close }: { id: string; ordinals: number[] | null; close: () => void }) {
  const [page, setPage] = useState(0); const [data, setData] = useState<EvidencePage | null>(null); const [error, setError] = useState("");
  useEffect(() => { if (ordinals?.length === 0) { setData({ total: 0, items: [] }); return; } let active = true; void api<EvidencePage>(`/imports/${id}/evidence?page=${page}${ordinals ? `&ordinals=${ordinals.join(",")}` : ""}`).then(x => { if (active) setData(x); }).catch(e => { if (active) setError(e.message); }); return () => { active = false; }; }, [id, page, ordinals]);
  return <div className="evidence-overlay"><section className="evidence-drawer" role="dialog" aria-modal="true" aria-label="Source evidence"><button onClick={close}>Close evidence</button><h2>Preserved source passages</h2>{error && <p className="text-error">{error}</p>}{data?.items.map(s => <blockquote key={s.id}><small>Passage {s.ordinal + 1} · {s.speaker} · {s.section}{s.timestamp ? ` · ${s.timestamp}` : ""}{s.artifact ? " · formatting artifact" : ""}</small><p className="prose">{s.text}</p></blockquote>)}{data?.total === 0 && <p>No evidence cited.</p>}<button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous passages</button>{" "}<button disabled={!data || (page + 1) * 20 >= data.total} onClick={() => setPage(page + 1)}>Next passages</button></section></div>;
}

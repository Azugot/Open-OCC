import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, post, downloadSource } from "./api";
import type { Job, Profiles } from "./api";
import ModelPicker from "./ModelPicker";
import { StructurePreview, TranscriptDrawer } from "./TranscriptViewer";
import ImportSteps from "./ImportSteps";
import EvidenceDrawer from "./SourceEvidenceDrawer";
import { IssueCard, ProposalCard, ResumeCard } from "./ImportEditors";
import type { Proposal, Resume, Issue } from "./ImportEditors";
import { useUnsaved } from "./UnsavedChanges";

type Blocker = { code: string; message: string; proposalId?: string; issueId?: string; field?: string };
type Draft = { job: Job; source: { id: string; fileName: string; sha256: string }; resume: Resume | null; issues: Issue[]; groups: { category: string; count: number }[]; conflicts: { proposalId: string; key: string; text: string }[]; approval: { canApprove: boolean; blockers: Blocker[] }; branch: { id: string; name: string }; campaignName: string };
const categories = ["fact", "character", "relationship", "knowledge", "world", "event", "inventory", "skill", "condition", "thread", "entity", "connection"];

export default function ImportReview({ id, expectedCheckpointId, onApproved, onReanalyzed, onNewSource }: { id: string; expectedCheckpointId: string; onApproved: () => Promise<void>; onReanalyzed: (id: string) => Promise<void>; onNewSource: () => void }) {
  const [draft, setDraft] = useState<Draft | null>(null); const revision = useRef(0); const readSequence = useRef(0);
  const [comparisonRequired, setComparisonRequired] = useState(false);
  const [rows, setRows] = useState<{ total: number; items: Proposal[] }>({ total: 0, items: [] });
  const [category, setCategory] = useState(""); const [query, setQuery] = useState(""); const [page, setPage] = useState(0); const [history, setHistory] = useState(false);
  const [view, setView] = useState<"attention" | "proposals" | "scene">("attention"); const [configure, setConfigure] = useState(false);
  const [busy, setBusy] = useState(false); const busyRef = useRef(false); const [error, setError] = useState(""); const [notice, setNotice] = useState(""); const [loadingRows, setLoadingRows] = useState(false);
  const [evidence, setEvidence] = useState<{ ordinals: number[] | null; proposalId?: string } | null>(null); const [browse, setBrowse] = useState(false);
  const [profiles, setProfiles] = useState<Profiles | null>(null); const [provider, setProvider] = useState(""); const [model, setModel] = useState("");
  const [maxCalls, setMaxCalls] = useState(100); const [inputLimit, setInputLimit] = useState(24000); const [outputLimit, setOutputLimit] = useState(3000);
  const { confirm, dirty } = useUnsaved();
  useEffect(() => { window.scrollTo({ top: 0 }); }, [view, page, category, history]);
  const load = useCallback(async (signal?: AbortSignal) => { const sequence = ++readSequence.current; const data = await api<Draft>(`/imports/${id}/draft`, { signal }); if (sequence === readSequence.current) { revision.current = data.job.proposalRevision; setDraft(data); } return data; }, [id]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal).catch(e => { if (e.name !== "AbortError") setError(e.message); }); void api<Profiles>("/providers", { signal: controller.signal }).then(setProfiles).catch(e => { if (e.name !== "AbortError") setError(e.message); }); return () => controller.abort(); }, [load]);
  useEffect(() => {
    const controller = new AbortController(); const timer = setTimeout(() => { setLoadingRows(true); void api<typeof rows>(`/imports/${id}/proposals?page=${page}&category=${encodeURIComponent(category)}&q=${encodeURIComponent(query)}&history=${history}`, { signal: controller.signal }).then(setRows).catch(e => { if (e.name !== "AbortError") setError(e.message); }).finally(() => { if (!controller.signal.aborted) setLoadingRows(false); }); }, 200);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [id, page, category, query, history, draft?.job.proposalRevision]);
  useEffect(() => {
    if (!draft || !["queued", "processing", "review"].includes(draft.job.status)) return;
    const controller = new AbortController(); let pending = false;
    const timer = setInterval(() => { if (pending || busyRef.current) return; pending = true; void load(controller.signal).catch(e => { if (e.name !== "AbortError") setError(e.message); }).finally(() => { pending = false; }); }, 2000);
    return () => { clearInterval(timer); controller.abort(); };
  }, [draft?.job.status, load]);
  useEffect(() => {
    if (!draft || !profiles) return;
    setMaxCalls(draft.job.maxCalls || 100); setInputLimit(draft.job.inputCharacterLimit || 24000); setOutputLimit(draft.job.outputTokenLimit || 3000);
    const choices = profiles.profiles.filter(p => p.enabled && p.adapter !== "fixture" && p.adapterConfigured !== false);
    const selected = choices.find(p => p.id === draft.job.provider) || choices.find(p => p.id === profiles.tasks.reconstruction) || choices[0];
    setProvider(selected?.id || ""); setModel(selected?.id === draft.job.provider ? draft.job.model : selected?.model || "");
  }, [draft?.job.id, profiles]);
  const mutate = async (path: string, value: unknown) => {
    if (comparisonRequired) throw Error("Compare refreshed data before saving again.");
    if (busyRef.current) throw Error("Wait for the current save to finish.");
    busyRef.current = true; setBusy(true);
    try { const result = await api<{ revision: number }>(path, { method: "PUT", body: JSON.stringify({ ...(value as object), revision: revision.current }) }); revision.current = result.revision; await load(); }
    catch (e) { if (e instanceof ApiError && e.status === 409) { setComparisonRequired(true); await load(); throw Error("Saved data changed. Your edits are retained. Compare the refreshed values before saving again."); } throw e; }
    finally { busyRef.current = false; setBusy(false); }
  };
  const perform = async (action: () => Promise<void>) => { setError(""); setNotice(""); try { await action(); setNotice("Saved."); } catch (e) { setError((e as Error).message); } };
  const move = (action: () => void) => void (async () => { if (!busy && await confirm()) action(); })();
  if (!draft) return <p role="status">{error || "Loading import…"}</p>;
  const editable = draft.job.status === "review" && draft.job.reviewCompleted;
  const grouped = draft.job.method === "agent-v3";
  const prepared = draft.job.status === "prepared"; const stopped = ["prepared", "paused", "cancelled", "failed"].includes(draft.job.status);
  const working = ["queued", "processing"].includes(draft.job.status); const structural = (code: string) => code.startsWith("state:") || ["resume:player", "resume:scene", "world:references"].includes(code);
  const profile = profiles?.profiles.find(p => p.id === provider);
  const modelReady = !!profile && profile.enabled && profile.adapter !== "fixture" && profile.adapterConfigured !== false && !!model.trim();
  const scene = draft.resume?.state;
  const blockers = draft.job.status === "approved" ? [] : draft.approval.blockers;
  const openBlocker = (blocker: Blocker) => move(() => { if (blocker.proposalId) { const conflict = draft.conflicts.find(x => x.proposalId === blocker.proposalId); const key = conflict?.key || blocker.code.replace(/^(claim:|reference:)/, ""); setQuery(key); setCategory(""); setPage(0); setView("proposals"); } else if (blocker.field === "proposals") setView("proposals"); else if (blocker.field) setView("scene"); else if (blocker.issueId) document.getElementById(`issue-${blocker.issueId}`)?.scrollIntoView({ behavior: "smooth" }); });
  const reanalyze = (forkOriginal: boolean) => move(() => void perform(async () => {
    setBusy(true); try { const job = await post<Job>(`/imports/${id}/reanalyze`, { inputCharacterLimit: inputLimit, outputTokenLimit: outputLimit, maxCalls, provider: provider || null, model: model || null, prepareOnly: true, forkOriginal }); await onReanalyzed(job.id); } finally { setBusy(false); }
  }));
  return <div className="import-review">
    <ImportSteps current={draft.job.status === "approved" ? 3 : editable ? 2 : working || configure || stopped && !prepared && draft.job.stage !== "normalize" ? 1 : 0} />
    <header className="import-summary"><h2>{draft.source.fileName}</h2><p>{draft.campaignName} · {draft.branch.name}</p></header>
    {error && <p role="alert" className="text-error">{error}</p>}{comparisonRequired && <section className="panel"><p>Compare the refreshed saved data with your retained entries.</p><details><summary>Refreshed saved values</summary><pre>{JSON.stringify({ resume: draft.resume, issues: draft.issues, proposals: rows.items }, null, 2)}</pre></details><button onClick={() => setComparisonRequired(false)}>I have compared the saved values</button></section>}{notice && <p role="status">{notice}</p>}
    {(prepared && !configure || draft.job.stage === "normalize") && <section className="panel"><h2>Check your source</h2>
      <p>Your original is preserved. Preview the readable text before choosing a reconstruction model.</p>
      {draft.job.total > 0 && <><StructurePreview sourceId={draft.source.id} jobId={id} /><button onClick={() => setBrowse(true)}>Read source preview</button></>}
      {draft.job.error && <p role="alert" className="text-error">{draft.job.error}</p>}
      {prepared && <button className="primary" onClick={() => setConfigure(true)}>Next: choose reconstruction model</button>}
      {draft.job.stage === "normalize" && <><button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/prepare`); await load(); })}>Retry source preview</button><button onClick={() => move(onNewSource)}>Choose a corrected file</button></>}
    </section>}
    {(working || stopped && (configure || !prepared && draft.job.stage !== "normalize")) && <section className="panel"><h2>{working ? "Reconstructing your story" : prepared ? "Choose how to reconstruct" : "Reconstruction paused"}</h2>
      <p>{draft.job.processed}/{draft.job.total || "?"} source {grouped ? "blocks" : "passages"} read · {stageName(draft.job.stage)}</p>
      <p>{draft.job.calls}/{draft.job.maxCalls} calls used. Reading all source text does not mean every detail has been recovered.</p>
      {draft.job.error && <p role="alert" className="text-error">{draft.job.error}</p>}
      {working ? <><p>You can leave this screen and return from import history. Processing continues in the background.</p><button disabled={busy} onClick={() => void perform(async () => { await post(`/imports/${id}/cancel`); await load(); })}>Pause reconstruction</button></> : <>
        <label>Reconstruction provider<select disabled={busy} value={provider} onChange={e => { setProvider(e.target.value); setModel(profiles?.profiles.find(p => p.id === e.target.value)?.model || ""); }}><option value="">Choose a configured provider</option>{profiles?.profiles.filter(p => p.enabled && p.adapter !== "fixture" && p.adapterConfigured !== false).map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        {provider && <ModelPicker profile={profile} value={model} onChange={setModel} disabled={busy} label="Reconstruction model" />}
        {!modelReady && <p>Configure an enabled model in Providers & settings to reconstruct this source. Your preview stays available.</p>}
        <Budgets inputLimit={inputLimit} outputLimit={outputLimit} maxCalls={maxCalls} setInputLimit={setInputLimit} setOutputLimit={setOutputLimit} setMaxCalls={setMaxCalls} disabled={busy} />
        <p>The selected provider receives the source text only after you start. Approval is required before it changes your story.</p>
        <div className="button-row"><button onClick={() => prepared ? setConfigure(false) : setBrowse(true)}>Back to source</button><button className="primary" disabled={busy || !modelReady || maxCalls <= draft.job.calls} onClick={() => void perform(async () => { setBusy(true); try { await post(`/imports/${id}/resume-processing`, { provider, model, maxCalls, inputCharacterLimit: inputLimit, outputTokenLimit: outputLimit }); await load(); } finally { setBusy(false); } })}>{prepared ? "Start reconstruction" : "Resume reconstruction"}</button></div>
        {maxCalls <= draft.job.calls && <p className="text-error">Increase the total call budget above the calls already used.</p>}
      </>}
    </section>}
    {(editable || draft.job.status === "approved") && <>
      <nav className="import-tabs" aria-label="Review views">{([['attention', 'Needs attention'], ['proposals', 'All proposals'], ['scene', 'Final scene']] as const).map(([tab, label]) => <button key={tab} aria-pressed={view === tab} className={view === tab ? "active" : ""} disabled={busy} onClick={() => move(() => setView(tab))}>{label}{tab === "attention" && ` (${blockers.length})`}</button>)}</nav>
      <section className="panel import-readiness"><h2>{draft.job.status === "approved" ? "Import approved" : draft.approval.canApprove ? "Ready for your approval" : `${draft.approval.blockers.length} things to review`}</h2>
        <p>{scene ? `${scene.location} · ${scene.time} · ${scene.objective}` : "The final scene still needs to be established."}</p>
        {view === "attention" && blockers.map((blocker, index) => <article className="import-item" key={`${blocker.code}-${index}`}><p>{blocker.message}</p>{(blocker.proposalId || blocker.issueId || blocker.field) && <button onClick={() => openBlocker(blocker)}>Review this item</button>}{blocker.code === "timeline" && <button onClick={() => reanalyze(true)}>Reconstruct on a new timeline</button>}</article>)}
        {editable && <><p>Approval saves the included proposals and final scene together. You can inspect all proposals before deciding.</p><button className="primary" disabled={busy || dirty || !draft.approval.canApprove} onClick={() => void perform(async () => { setBusy(true); try { await post(`/imports/${id}/approval`, { expectedCheckpointId, revision: revision.current }); await onApproved(); } catch (e) { await load(); throw e; } finally { setBusy(false); } })}>Approve and continue story</button>{dirty && <p>Save or discard your pending edits before approval.</p>}</>}
        {draft.job.status === "approved" && <button onClick={() => void onApproved()}>Continue story</button>}
      </section>
      {view === "attention" && <section className="panel"><h2>Source uncertainties and notes</h2><p>Acknowledgment records what the source cannot establish. It cannot replace correcting invalid story data.</p>{draft.issues.filter(x => !structural(x.code)).map(issue => <IssueCard key={issue.id} issue={issue} disabled={!editable || busy} evidence={() => setEvidence({ ordinals: JSON.parse(issue.evidenceJson) })} save={resolution => mutate(`/imports/${id}/issues/${issue.id}`, { resolution })} report={perform} />)}{!draft.issues.some(x => !structural(x.code)) && <p>No additional uncertainties were reported.</p>}</section>}
      {view === "proposals" && <section className="panel"><h2>All proposed story details</h2><div className="import-filters"><label>Search<input value={query} maxLength={200} onChange={e => { const value = e.target.value; move(() => { setQuery(value); setPage(0); }); }} placeholder="Names, claims, events…" /></label><label>Category<select value={category} onChange={e => { const value = e.target.value; move(() => { setCategory(value); setPage(0); }); }}><option value="">All categories</option>{categories.map(c => <option key={c} value={c}>{c} ({draft.groups.find(g => g.category === c)?.count || 0})</option>)}</select></label><label className="check-label"><input type="checkbox" checked={history} onChange={e => { const checked = e.target.checked; move(() => { setHistory(checked); setPage(0); }); }} />Show previous versions</label></div>
        <p role="status">{loadingRows ? "Loading proposals…" : `${rows.total} proposals · page ${page + 1}`}</p>
        {rows.items.map(row => <ProposalCard key={row.proposal.id} row={row} conflict={draft.conflicts.find(x => x.proposalId === row.proposal.id)?.text} disabled={!editable || busy || !row.proposal.current} evidence={() => setEvidence({ ordinals: null, proposalId: row.proposal.id })} save={(claim, excluded, syncResume) => mutate(`/imports/${id}/proposals/${row.proposal.id}`, { claim, excluded, syncResume })} report={perform} />)}
        {!loadingRows && !rows.items.length && <p>No proposals match this view.</p>}<div className="button-row"><button disabled={busy || loadingRows || page === 0} onClick={() => move(() => setPage(page - 1))}>Previous proposals</button><button disabled={busy || loadingRows || (page + 1) * 20 >= rows.total} onClick={() => move(() => setPage(page + 1))}>Next proposals</button></div>
      </section>}
      <div hidden={view !== "scene"}><ResumeCard resume={draft.resume} grouped={grouped} disabled={!editable || busy} evidence={() => setEvidence({ ordinals: draft.resume?.evidenceOrdinals || [] })} save={resume => mutate(`/imports/${id}/resume`, { resume })} report={perform} /></div>
    </>}
    <details className="panel"><summary>Source details and advanced actions</summary><button onClick={() => void perform(async () => downloadSource(draft.source.id, draft.source.fileName))}>Download original</button>{draft.job.total > 0 && <button onClick={() => setBrowse(true)}>Read preserved source</button>}<small className="hash">SHA-256 · {draft.source.sha256}</small><p>Captured model: {draft.job.provider} / {draft.job.model}. Reported tokens: {draft.job.inputTokens ?? "unavailable"} input / {draft.job.outputTokens ?? "unavailable"} output.</p>
      {!working && <><Budgets inputLimit={inputLimit} outputLimit={outputLimit} maxCalls={maxCalls} setInputLimit={setInputLimit} setOutputLimit={setOutputLimit} setMaxCalls={setMaxCalls} disabled={busy} /><p>Fresh reconstruction prepares a new draft from this source. The current draft stays available.</p><button disabled={busy} onClick={() => reanalyze(false)}>Prepare fresh reconstruction</button></>}
    </details>
    {evidence && <EvidenceDrawer key={JSON.stringify(evidence)} id={id} ordinals={evidence.ordinals} proposalId={evidence.proposalId} grouped={grouped} close={() => setEvidence(null)} />}
    {browse && <TranscriptDrawer sourceId={draft.source.id} jobId={id} close={() => setBrowse(false)} />}
  </div>;
}
function stageName(stage?: string) { return ({ normalize: "Preparing source", extract: "Reading the source", reconcile: "Reconciling details", resume: "Recovering the final scene", audit: "Checking evidence", repair: "Repairing inconsistencies", complete: "Ready for review" } as Record<string, string>)[stage || ""] || stage || "Waiting"; }
function Budgets({ inputLimit, outputLimit, maxCalls, setInputLimit, setOutputLimit, setMaxCalls, disabled }: { inputLimit: number; outputLimit: number; maxCalls: number; setInputLimit: (x: number) => void; setOutputLimit: (x: number) => void; setMaxCalls: (x: number) => void; disabled: boolean }) { return <details><summary>Advanced: processing budgets</summary>{[["Input characters per call", inputLimit, 12000, 120000, setInputLimit], ["Output tokens per call", outputLimit, 1000, 12000, setOutputLimit], ["Total model calls", maxCalls, 1, 1000, setMaxCalls]].map(([label, value, min, max, change]) => <label key={label as string}>{label as string}<input disabled={disabled} type="number" min={min as number} max={max as number} value={value as number} onChange={e => (change as (x: number) => void)(Number(e.target.value))} /></label>)}<small>Calls include analysis, review, and repairs. Processing pauses when the budget is used. No provider cost estimate is assumed.</small></details>; }

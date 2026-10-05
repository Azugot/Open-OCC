import { useState } from "react";
import { api } from "./api";
import type { Job } from "./api";
import ImportSteps from "./ImportSteps";

export default function ImportSource({ branchId, checkpointId, campaignName, timelineName, freshCampaign, onPrepared }: { branchId: string; checkpointId: string; campaignName: string; timelineName: string; freshCampaign: boolean; onPrepared: (job: Job) => Promise<void> }) {
  const [file, setFile] = useState<File | null>(null); const [destination, setDestination] = useState(freshCampaign ? "current" : "new");
  const [name, setName] = useState("Imported story"); const [busy, setBusy] = useState(false); const [error, setError] = useState("");
  const validate = (value: File | null) => !value ? "Choose a transcript file." : value.size === 0 || value.size > 32 * 1024 * 1024 ? "Choose a nonempty file up to 32 MiB." : value.name.length > 200 ? "The filename must be at most 200 characters." : !/\.(txt|md|markdown|html|htm|docx|json)$/i.test(value.name) ? "Choose TXT, Markdown, HTML, DOCX, or documented JSON." : "";
  return <><ImportSteps current={0} /><form className="upload-panel import-source" onSubmit={e => { e.preventDefault(); void (async () => {
    const problem = validate(file); if (problem) { setError(problem); return; }
    setBusy(true); setError(""); try {
      const body = new FormData(); body.append("file", file!); body.append("prepareOnly", "true"); body.append("destination", destination); body.append("timelineName", name); body.append("checkpointId", checkpointId);
      await onPrepared(await api<Job>(`/branches/${branchId}/imports`, { method: "POST", body }));
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  })(); }}><h2>Choose your source and destination</h2><p>{campaignName} · {timelineName}</p><p>Preserve the original and inspect its text. No model is needed for this step.</p>
    <label>Transcript file<input type="file" accept=".txt,.md,.markdown,.html,.htm,.docx,.json" disabled={busy} onChange={e => { const chosen = e.target.files?.[0] ?? null; setFile(chosen); setError(chosen ? validate(chosen) : ""); }} /></label>
    <small>TXT, Markdown, HTML, DOCX, or documented JSON · up to 32 MiB per file and 16 MiB of extracted text. Images and ZIP archives are not transcript sources.</small>
    <label>Destination timeline<select disabled={busy} value={destination} onChange={e => setDestination(e.target.value)}><option value="new">New timeline — preserve the current story</option><option value="current">Current timeline: {timelineName}</option></select></label>
    {destination === "new" ? <label>New timeline name<input value={name} disabled={busy} required maxLength={100} onChange={e => setName(e.target.value)} /><small>Starts from the selected checkpoint; approval updates the new timeline.</small></label> : <p>Approval applies the reconstructed scene to this timeline. It cannot overwrite a story that advances during review.</p>}
    <details><summary>JSON format example</summary><p>Use a root messages array containing string role and content fields. Timestamp is optional.</p><pre>{JSON.stringify({ messages: [{ role: "user", content: "Mara enters the harbor." }, { role: "assistant", content: "Lyra waits beside the boat." }] }, null, 2)}</pre></details>
    {error && <p role="alert" className="text-error">{error}</p>}<button className="primary" disabled={busy || !file || !!validate(file) || destination === "new" && !name.trim()}>{busy ? "Preserving source…" : "Preserve and preview"}</button>
  </form></>;
}

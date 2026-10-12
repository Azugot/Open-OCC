import { useEffect, useState } from "react";
import type { Job } from "./api";
import ActivityMessage from "./ActivityMessage";
import "./progress.css";

export type ReconstructionProgress = {
  percent: number; stage: string; completed: number; total: number | null;
  activity: "queued" | "generating" | "repairing-response" | "organizing";
  requestStartedAt: string | null; estimatedRemainingMinSeconds: number | null;
  estimatedRemainingMaxSeconds: number | null; takingLonger: boolean; asOf: string;
};
const stages = [
  { key: "extract", name: "Read source", doing: "Reading the transcript and recovering characters, events, inventory, and skills." },
  { key: "reconcile", name: "Reconcile details", doing: "Combining repeated details and checking changes against the source." },
  { key: "resume", name: "Recover final scene", doing: "Recovering the last location, participants, player action, and current story state." },
  { key: "audit", name: "Check evidence", doing: "Checking each group of story details and the final scene against their supporting passages." },
  { key: "repair", name: "Resolve inconsistencies", doing: "Attempting a focused correction. Any remaining uncertainties will be shown for your review." },
];
function duration(seconds: number) {
  if (seconds < 60) return `${Math.max(1, Math.ceil(seconds))} sec`;
  if (seconds < 3600) return `${Math.ceil(seconds / 60)} min`;
  return `${Math.floor(seconds / 3600)} hr ${Math.ceil(seconds % 3600 / 60)} min`;
}
export default function ImportProgress({ job, progress, grouped }: { job: Job; progress?: ReconstructionProgress; grouped: boolean }) {
  const [now, setNow] = useState(Date.now());
  const [phaseClock, setPhaseClock] = useState(() => new Date().toISOString());
  const working = ["queued", "processing", "preparing"].includes(job.status);
  const waitingToResume = job.status === "queued" && job.processed > 0;
  const phraseGroup = job.status === "queued" ? waitingToResume ? "resuming" : "import-queued" : progress?.activity === "repairing-response" ? "import-repair" : `import-${job.stage === "normalize" ? "preparing" : job.stage || "preparing"}`;
  useEffect(() => setPhaseClock(new Date().toISOString()), [phraseGroup, job.id]);
  useEffect(() => { if (!working) return; const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer); }, [working]);
  const index = stages.findIndex(x => x.key === job.stage);
  const current = stages[index];
  const started = progress?.requestStartedAt ? Date.parse(progress.requestStartedAt) : NaN;
  const elapsed = Number.isFinite(started) ? Math.max(0, (now - started) / 1000) : null;
  const min = progress?.estimatedRemainingMinSeconds;
  const max = progress?.estimatedRemainingMaxSeconds;
  const estimate = working && min != null && max != null;
  const clock = (seconds: number) => new Date(Date.parse(progress!.asOf) + seconds * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  const unit = job.stage === "extract" ? `source ${grouped ? "blocks" : "passages"}` : "review batches";
  const stopped = ["paused", "cancelled", "failed"].includes(job.status);
  return <div className="reconstruction-progress">
    <div className="reconstruction-progress-heading"><strong role="status" aria-live="polite" aria-atomic="true">{job.status === "queued" ? waitingToResume ? "Waiting to resume saved work" : "Waiting to start" : stopped ? `${job.status === "paused" ? "Paused" : job.status === "cancelled" ? "Cancelled" : "Failed"}: ${current?.name || "Prepare source"}` : current?.name || "Preparing source"}</strong><span>{progress?.percent ?? 0}% · estimated progress</span></div>
    <progress className={working ? "is-working" : ""} max={100} value={progress?.percent ?? 0} aria-label="Overall reconstruction progress" />
    <ol className="reconstruction-stages" aria-label="Reconstruction stages">{stages.map((stage, i) => <li key={stage.key} className={i < index ? "complete" : i === index ? "active" : ""} aria-current={i === index ? "step" : undefined}><span aria-hidden="true">{i < index ? "✓" : i + 1}</span>{stage.name}</li>)}</ol>
    <p role="status">{job.status === "queued" ? "Waiting for the reconstruction worker." : stopped ? "Processing has stopped. Resume below to continue from this saved stage." : current?.doing || "Turning the preserved file into readable source blocks."}</p>
    {working && <ActivityMessage group={phraseGroup} active={working} startedAt={job.createdAt || phaseClock} phaseStartedAt={phaseClock} seed={job.id} />}
    {progress?.total != null && ["extract", "reconcile", "audit"].includes(job.stage || "") && <p>{progress.completed}/{progress.total} {unit} completed.</p>}
    {job.processed >= job.total && job.total > 0 && <p className="progress-detail">All {job.total} source {grouped ? "blocks" : "passages"} read. Reading is the first stage; reconstruction must finish before review.</p>}
    {working && progress?.activity === "generating" && <p className="progress-detail"><span className="progress-pulse" aria-hidden="true" />Model is generating a response{elapsed != null ? ` · ${duration(elapsed)} elapsed on this request` : ""}.</p>}
    {working && progress?.activity === "repairing-response" && <p className="progress-detail"><span className="progress-pulse" aria-hidden="true" />Correcting a response that failed validation{elapsed != null ? ` · ${duration(elapsed)} elapsed on this request` : ""}.</p>}
    {working && progress?.activity === "organizing" && <p className="progress-detail">Saving results and organizing the next batch.</p>}
    {working && <div className="reconstruction-eta"><strong>Estimated completion</strong><p>{estimate ? `Around ${clock(min)}–${clock(max)} · about ${duration(min)}–${duration(max)} remaining` : job.status === "queued" ? "Available once processing starts." : progress?.takingLonger ? "This request is taking longer than recent requests. The completion estimate will update when it returns." : "Calculating — waiting for enough completed requests or a review batch plan."}</p><small>{estimate ? "Based on recent requests. Model speed, additional review batches, and response repairs can change this range." : "The current activity and elapsed request time continue updating while the model works."}</small></div>}
    <p className="progress-detail">{job.calls}/{job.maxCalls} model calls used · {job.provider} / {job.model}</p>
  </div>;
}

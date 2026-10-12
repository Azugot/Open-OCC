import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "./api";
import type { GenerationProgressData, TurnStreamEvent } from "./api";
import ActivityMessage from "./ActivityMessage";
import { phaseLabels, safeStoryPhase } from "./activityMessages";
import "./progress.css";

type ActiveRun = { id: string; status: string; contextJson?: string };
type Operation = { token: number; branchId: string; checkpointId: string; mode: string; runId?: string; startedAt: string; loadingSaved: boolean };
export function useGenerationProgress(branchId: string | undefined, checkpointId: string | undefined, activeRun: ActiveRun | undefined, onSettled: () => Promise<void>) {
  const [operation, setOperation] = useState<Operation | null>(null);
  const operationRef = useRef(operation); operationRef.current = operation;
  const branchRef = useRef(branchId); branchRef.current = branchId;
  const [saved, setSaved] = useState<{ branchId: string; value: GenerationProgressData } | null>(null);
  const [connectionError, setConnectionError] = useState("");
  const counter = useRef(0); const settled = useRef(""); const settledCallback = useRef(onSettled); settledCallback.current = onSettled;
  const sequence = useRef(0);
  const change = (value: Operation | null) => { operationRef.current = value; setOperation(value); };
  const local = operation?.branchId === branchId ? operation : null;
  const runId = local?.runId || activeRun?.id;
  const accept = useCallback((value: GenerationProgressData, branch: string) => setSaved(old => {
    if (old?.branchId === branch && old.value.runId === value.runId && Date.parse(old.value.updatedAt) > Date.parse(value.updatedAt)) return old;
    return { branchId: branch, value };
  }), []);
  useEffect(() => {
    if (!branchId || (!local && !activeRun) || local?.loadingSaved) return;
    const controller = new AbortController(); const seq = ++sequence.current; let timer: ReturnType<typeof setTimeout>;
    const read = async () => {
      try {
        const query = runId ? `runId=${encodeURIComponent(runId)}` : `expectedCheckpointId=${encodeURIComponent(local?.checkpointId || checkpointId || "")}`;
        const value = await api<GenerationProgressData | null>(`/branches/${branchId}/generation-progress?${query}`, { signal: controller.signal, cache: "no-store" });
        if (seq !== sequence.current || controller.signal.aborted) return;
        setConnectionError("");
        // A retry/new request can briefly see the previous stopped run before the server reserves it.
        if (value && (!local || Date.parse(value.updatedAt) >= Date.parse(local.startedAt))) {
          accept(value, branchId);
          if (value.status !== "running") {
            if (!local && settled.current !== `${value.runId}:${value.status}`) { await settledCallback.current(); settled.current = `${value.runId}:${value.status}`; }
            return;
          }
        }
      } catch (e) {
        if (controller.signal.aborted || seq !== sequence.current) return;
        setConnectionError("Progress updates are temporarily unavailable. Waiting to reconnect…");
      }
      if (seq === sequence.current && !controller.signal.aborted) timer = setTimeout(() => void read(), 1000);
    };
    void read();
    return () => { controller.abort(); clearTimeout(timer); sequence.current++; };
  }, [branchId, checkpointId, runId, local?.token, local?.loadingSaved, activeRun?.status, accept]);

  const begin = (mode: string, id?: string) => {
    const token = ++counter.current;
    change({ token, branchId: branchId!, checkpointId: checkpointId!, mode, runId: id, startedAt: new Date().toISOString(), loadingSaved: false });
    setSaved(null); setConnectionError(""); return token;
  };
  const loadingSaved = (token: number) => { const current = operationRef.current; if (current?.token === token) change({ ...current, loadingSaved: true }); };
  const finish = (token: number) => { if (operationRef.current?.token === token) change(null); };
  const streamEvent = (event: TurnStreamEvent, token: number) => {
    const current = operationRef.current; if (current?.token !== token || current.branchId !== branchRef.current) return;
    if (event.type === "start" && event.runId) change({ ...current, runId: event.runId });
    if (event.type === "progress" && event.progress) accept(event.progress, current.branchId);
    if (event.type === "done") loadingSaved(token);
  };
  const value = saved && saved.branchId === branchId && (!runId || saved.value.runId === runId) && (!local || Date.parse(saved.value.updatedAt) >= Date.parse(local.startedAt)) ? saved.value : null;
  let progress: GenerationProgressData | null = null;
  if (local && (local.loadingSaved || value?.status === "completed")) progress = { runId: local.runId || "", mode: local.mode, status: "running", phase: "loading-saved", startedAt: local.startedAt, phaseStartedAt: local.startedAt, updatedAt: local.startedAt, asOf: new Date().toISOString() };
  else if (local || activeRun) progress = value || { runId: runId || "", status: local ? "running" : activeRun!.status, mode: local?.mode || (activeRun?.contextJson && activeRun.contextJson !== "{}" ? "narrator" : "autonomous"), phase: local?.runId ? "resuming" : "preparing", startedAt: local?.startedAt || new Date().toISOString(), phaseStartedAt: local?.startedAt || new Date().toISOString(), updatedAt: local?.startedAt || new Date().toISOString(), asOf: new Date().toISOString() };
  return { progress, connectionError, begin, loadingSaved, finish, streamEvent };
}

export default function GenerationProgress({ progress, connectionError = "" }: { progress: GenerationProgressData | null; connectionError?: string }) {
  if (!progress) return null;
  const active = progress.status === "running";
  const phase = safeStoryPhase(progress.phase, progress.mode);
  const label = active ? phaseLabels[phase] : `Generation ${progress.status}`;
  return <section className={`generation-progress ${active ? "is-active" : ""}`} aria-label="Story processing">
    <div className="generation-progress-heading"><span className="activity-spinner" aria-hidden="true" /><strong role="status" aria-live="polite" aria-atomic="true">{label}</strong></div>
    {<ActivityMessage message={phase === "loading-saved" && active ? "Loading the saved story and its updated checkpoint…" : undefined} group={phase === "resuming" ? "story-resuming" : phase} active={active} startedAt={progress.startedAt} phaseStartedAt={progress.phaseStartedAt} stoppedAt={progress.updatedAt} seed={progress.runId} />}
    {connectionError && <small role="status">{connectionError}</small>}
  </section>;
}

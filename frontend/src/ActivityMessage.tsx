import { useEffect, useState } from "react";
import { activityPhrase, elapsedText } from "./activityMessages";

export default function ActivityMessage({ group, active, startedAt, phaseStartedAt, stoppedAt, seed = "", message }: {
  group: string; active: boolean; startedAt: string; phaseStartedAt?: string; stoppedAt?: string; seed?: string; message?: string;
}) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => { if (!active) return; setNow(Date.now()); const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer); }, [active, group, phaseStartedAt]);
  const at = active ? now : Date.parse(stoppedAt || startedAt);
  const elapsed = Math.max(0, at - Date.parse(startedAt));
  const phaseElapsed = Math.max(0, at - Date.parse(phaseStartedAt || startedAt));
  return <div className="activity-message"><p>{message || (active ? activityPhrase(group, phaseElapsed, seed) : "Processing has stopped. Your committed story is preserved.")}</p><small>{elapsedText(Number.isFinite(elapsed) ? elapsed : 0)} elapsed</small></div>;
}

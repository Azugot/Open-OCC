export async function api<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const headers = new Headers(options.headers);
  if (options.method && !["GET", "HEAD"].includes(options.method.toUpperCase()))
    headers.set("X-Open-OCC", "1");
  const token = sessionStorage.getItem("occ-token");
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (options.body && !(options.body instanceof FormData))
    headers.set("Content-Type", "application/json");
  const response = await fetch(`/api${path}`, { ...options, headers });
  if (!response.ok) {
    const payload = await response.json().catch(() => null);
    throw new Error(
      payload?.error ||
        `Request failed (${response.status}). Check that the backend is running.`,
    );
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
export const post = <T>(path: string, value?: unknown, signal?: AbortSignal) =>
  api<T>(path, {
    method: "POST",
    body: value === undefined ? undefined : JSON.stringify(value),
    signal,
  });
export async function streamTurn(
  path: string,
  value: unknown,
  onDelta: (text: string) => void,
  signal: AbortSignal,
) {
  const headers = new Headers({
    "Content-Type": "application/json",
    "X-Open-OCC": "1",
  });
  const token = sessionStorage.getItem("occ-token");
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const response = await fetch(`/api${path}`, {
    method: "POST",
    headers,
    body: JSON.stringify(value),
    signal,
  });
  if (!response.ok || !response.body) {
    const payload = await response.json().catch(() => null);
    throw new Error(
      payload?.error || `Generation failed (${response.status}).`,
    );
  }
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let pending = "";
  for (;;) {
    const { value: chunk = "", done } = await reader.read();
    pending += chunk;
    const lines = pending.split("\n");
    pending = lines.pop() || "";
    for (const line of lines) {
      if (!line.trim()) continue;
      const event = JSON.parse(line);
      if (event.type === "delta") onDelta(event.text);
      if (event.type === "error") throw new Error(event.error);
    }
    if (done) break;
  }
}
export async function downloadSource(id: string, name: string) {
  const headers = new Headers();
  const token = sessionStorage.getItem("occ-token");
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const r = await fetch(`/api/sources/${id}/download`, { headers });
  if (!r.ok)
    throw new Error("Source download failed. Check your access token.");
  const url = URL.createObjectURL(await r.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export async function downloadCampaignExport(id: string, name: string) {
  const headers = new Headers();
  const token = sessionStorage.getItem("occ-token");
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const r = await fetch(`/api/campaigns/${id}/export`, { headers });
  if (!r.ok) throw new Error("Campaign export failed.");
  const url = URL.createObjectURL(await r.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = `${name.replace(/[^a-z0-9-_]+/gi, "-")}.json`;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export type Branch = {
  id: string;
  name: string;
  campaignId: string;
  headCheckpointId: string;
};
export type Campaign = {
  id: string;
  name: string;
  createdAt: string;
  branches: Branch[];
};
export type State = {
  location: string;
  time: string;
  objective: string;
  condition: string;
  skills: Record<string, string>;
  inventory: Record<string, number>;
  participants: string[];
};
export type Fact = {
  id: string;
  text: string;
  visibility: string;
  reviewStatus: string;
  provenance: string;
  kind: string;
  confidence: number;
  knownByJson: string;
};
export type Thread = {
  id?: string;
  title: string;
  details: string;
  kind: string;
  status: string;
  importance: number;
};
export type Workspace = {
  campaign: Campaign;
  branch: Branch;
  state: State;
  checkpoint: { id: string };
  branches: Branch[];
  checkpoints: {
    id: string;
    sequence: number;
    label: string;
    createdAt: string;
  }[];
  messages: {
    id: string;
    role: string;
    content: string;
    provider: string;
    model: string;
  }[];
  facts: Fact[];
  threads: Thread[];
  runs: { id: string; status: string; error?: string; contextJson?: string }[];
  world?: {
    world: { id: string; name: string; description: string };
    version: { id: string; version: number; authorInstructions: string };
  } | null;
  characters?: {
    id: string;
    name: string;
    description: string;
    goals: string;
    status: string;
  }[];
  relationships?: {
    id: string;
    fromCharacterId: string;
    toCharacterId: string;
    label: string;
    score: number;
    notes: string;
  }[];
  knowledge?: {
    id: string;
    characterId: string;
    subject: string;
    beliefType: string;
    confidence: number;
  }[];
  mechanics?: {
    id: string;
    kind: string;
    subject: string;
    delta: number;
    reason: string;
    sequence: number;
  }[];
  events?: { id: string; type: string; summary: string; sequence: number }[];
  summary?: { text: string; throughSequence: number } | null;
};
export type Job = {
  id: string;
  sourceId: string;
  status: string;
  processed: number;
  total: number;
  error?: string;
  expectedCheckpointId: string;
  method: string;
  provider: string;
  model: string;
  proposedStateJson?: string;
  proposedThreadsJson: string;
  summary?: string;
  stage?: string;
  proposalRevision: number;
  calls: number;
  maxCalls: number;
  inputCharacterLimit: number;
  outputTokenLimit: number;
  inputTokens?: number;
  outputTokens?: number;
  reviewCompleted: boolean;
  resumeJson?: string;
};
export type Review = {
  job: Job;
  source: { id: string; fileName: string; sha256: string; size: number };
  candidates: {
    fact: Fact;
    evidence: { id: string; ordinal: number; speaker: string; text: string }[];
  }[];
};
export type RetrievalHit = {
  type: string;
  id: string;
  text: string;
  sequence: number;
  score: number;
  source?: string;
};
export type Profiles = {
  tasks: { narration: string; reconstruction: string; memory: string };
  profiles: {
    id: string;
    name: string;
    adapter: string;
    model: string;
    enabled: boolean;
    configured: boolean;
    capabilities: { available: boolean; note: string };
    agentCapabilities?: {
      available?: boolean;
      supported?: boolean;
      note: string;
    };
  }[];
};

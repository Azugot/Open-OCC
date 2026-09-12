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
  runs: { id: string; status: string; error?: string }[];
};
export type Job = {
  id: string;
  sourceId: string;
  status: string;
  processed: number;
  total: number;
  error?: string;
  expectedCheckpointId: string;
};
export type Review = {
  job: Job;
  source: { id: string; fileName: string; sha256: string; size: number };
  candidates: {
    fact: Fact;
    evidence: { id: string; ordinal: number; speaker: string; text: string }[];
  }[];
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
  }[];
};

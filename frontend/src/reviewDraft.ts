// Adopt server changes only in untouched fields. Keep corrections and explicitly flag overlapping changes.
export function mergeDraft<T>(before: T, local: T, remote: T): { value: T; conflict: boolean } {
  if (JSON.stringify(before) === JSON.stringify(local)) return { value: remote, conflict: false };
  if (JSON.stringify(before) === JSON.stringify(remote) || JSON.stringify(local) === JSON.stringify(remote)) return { value: local, conflict: false };
  if (before && local && remote && typeof before === "object" && typeof local === "object" && typeof remote === "object" && !Array.isArray(local)) {
    const a = before as Record<string, unknown>, b = local as Record<string, unknown>, c = remote as Record<string, unknown>;
    const value: Record<string, unknown> = {}; let conflict = false;
    for (const key of new Set([...Object.keys(a), ...Object.keys(b), ...Object.keys(c)])) { const part = mergeDraft(a[key], b[key], c[key]); value[key] = part.value; conflict ||= part.conflict; }
    return { value: value as T, conflict };
  }
  return { value: local, conflict: true };
}


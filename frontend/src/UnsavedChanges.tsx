import { mergeDraft } from "./reviewDraft";
import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";

type Editor = { save: () => Promise<void>; discard: () => void; label: string };
type Registry = { register: (id: string, editor: Editor | null) => void; confirm: () => Promise<boolean>; dirty: boolean };
const Context = createContext<Registry | null>(null);

export function UnsavedProvider({ children }: { children: ReactNode }) {
  const entries = useRef(new Map<string, Editor>());
  const [dirty, setDirty] = useState(false);
  const [pending, setPending] = useState<((answer: boolean) => void) | null>(null);
  const [saving, setSaving] = useState(false); const [error, setError] = useState("");
  const register = useCallback((id: string, editor: Editor | null) => {
    if (editor) entries.current.set(id, editor); else entries.current.delete(id);
    setDirty(entries.current.size > 0);
  }, []);
  const confirm = useCallback(() => entries.current.size === 0 ? Promise.resolve(true) : new Promise<boolean>(resolve => {
    setError(""); setPending(() => resolve);
  }), []);
  useEffect(() => {
    if (!dirty) return;
    const unload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", unload); return () => window.removeEventListener("beforeunload", unload);
  }, [dirty]);
  const finish = (answer: boolean) => { pending?.(answer); setPending(null); };
  return <Context.Provider value={{ register, confirm, dirty }}>{children}
    {pending && <div className="modal-backdrop"><section className="modal unsaved-dialog" role="dialog" aria-modal="true" aria-labelledby="unsaved-title" onKeyDown={e => {
      if (e.key === "Escape" && !saving) finish(false);
      if (e.key === "Tab") { const buttons = Array.from(e.currentTarget.querySelectorAll<HTMLButtonElement>("button:not(:disabled)")); if (e.shiftKey && document.activeElement === buttons[0]) { e.preventDefault(); buttons.at(-1)?.focus(); } else if (!e.shiftKey && document.activeElement === buttons.at(-1)) { e.preventDefault(); buttons[0]?.focus(); } }
    }}><h2 id="unsaved-title">Keep your edits?</h2><p>You have unsaved changes. Save them before continuing, discard them, or stay here.</p>
      {error && <p role="alert" className="text-error">{error}</p>}
      <div className="button-row"><button autoFocus disabled={saving} onClick={() => finish(false)}>Stay</button>
      <button disabled={saving} onClick={() => { for (const editor of entries.current.values()) editor.discard(); entries.current.clear(); setDirty(false); finish(true); }}>Discard changes</button>
      <button className="primary" disabled={saving} onClick={() => void (async () => {
        setSaving(true); try { for (const [id, editor] of [...entries.current]) { await editor.save(); entries.current.delete(id); } setDirty(false); finish(true); }
        catch (e) { setError((e as Error).message); } finally { setSaving(false); }
      })()}>{saving ? "Saving…" : "Save and continue"}</button></div>
    </section></div>}
  </Context.Provider>;
}
export function useUnsaved() { const value = useContext(Context); if (!value) throw Error("Missing unsaved changes provider"); return value; }

export function useEditor<T>(id: string, label: string, server: T, save: (value: T) => Promise<void>) {
  const { register } = useUnsaved(); const baseline = useRef(server);
  const [value, setValue] = useState(server); const [conflict, setConflict] = useState(false);
  const valueRef = useRef(value); valueRef.current = value;
  const saveRef = useRef(save); saveRef.current = save;
  const conflictRef = useRef(conflict); conflictRef.current = conflict;
  const serialized = JSON.stringify(server);
  useEffect(() => {
    const merged = mergeDraft(baseline.current, valueRef.current, server);
    baseline.current = server; valueRef.current = merged.value; setValue(merged.value); setConflict(x => x || merged.conflict);
  }, [serialized]);
  const dirty = JSON.stringify(value) !== JSON.stringify(baseline.current);
  const commit = useCallback(async () => {
    if (conflictRef.current) throw Error("Compare the refreshed saved values before keeping your edits.");
    const current = valueRef.current; await saveRef.current(current); baseline.current = current; register(id, null); setValue(valueRef.current);
  }, [id, register]);
  const discard = useCallback(() => { valueRef.current = baseline.current; setValue(baseline.current); setConflict(false); }, []);
  useEffect(() => { register(id, dirty ? { label, save: commit, discard } : null); return () => register(id, null); }, [id, dirty, label, commit, discard, register]);
  return { value, setValue, dirty, conflict, commit, discard, acceptLocal: () => setConflict(false), saved: baseline.current };
}


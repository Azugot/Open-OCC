import { useEffect, useRef, useState } from "react";
import { api } from "./api";

export default function DeleteStoryModal({ id, name, onCancel, onDeleted }: {
  id: string; name: string; onCancel: () => void; onDeleted: () => void;
}) {
  const dialog = useRef<HTMLDivElement>(null);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.querySelector<HTMLButtonElement>("button")?.focus();
    return () => { if (previous?.isConnected) previous.focus(); };
  }, []);
  const remove = async () => {
    setDeleting(true); setError("");
    try { await api(`/campaigns/${id}`, { method: "DELETE" }); onDeleted(); }
    catch (e) { setError(e instanceof Error ? e.message : "Story deletion failed."); setDeleting(false); }
  };
  return <div className="modal-backdrop">
    <div ref={dialog} className="modal" role="alertdialog" aria-modal="true" aria-labelledby="delete-story-title" aria-describedby="delete-story-description" aria-busy={deleting}
      onKeyDown={e => {
        if (e.key === "Escape" && !deleting) { e.preventDefault(); onCancel(); }
        if (e.key === "Tab") {
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
          const first = buttons[0], last = buttons[buttons.length - 1];
          if (!first) { e.preventDefault(); return; }
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
      }}>
      <h2 id="delete-story-title">Delete this story?</h2>
      <p id="delete-story-description">“{name}” and all its timelines, checkpoints, imported sources, graph history, and memories will be permanently deleted. This cannot be undone.</p>
      {error && <p role="alert" className="delete-error">{error}</p>}
      <div className="delete-actions">
        <button type="button" disabled={deleting} onClick={onCancel}>Keep story</button>
        <button type="button" className="danger" disabled={deleting} onClick={() => void remove()}>{deleting ? "Deleting…" : "Delete story"}</button>
      </div>
    </div>
  </div>;
}

import { useEffect, useState } from "react";
import { api } from "./api";

type Catalog = {
  supported: boolean;
  models: { id: string; name: string; chatCapable: boolean; downloaded?: boolean | null }[];
  error?: string | null;
};

export default function ModelPicker({ profile, value, onChange, disabled = false, label = "Model", defaultLabel = "Enter a model ID" }: {
  profile?: { id: string; adapter: string };
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  label?: string;
  defaultLabel?: string;
}) {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [revision, setRevision] = useState(0);
  const discoverable = profile?.id === "deepseek" || profile?.adapter === "lemonade";
  useEffect(() => {
    setCatalog(null); setError("");
    if (!discoverable || !profile) { setLoading(false); return; }
    const controller = new AbortController();
    setLoading(true);
    void api<Catalog>(`/providers/${encodeURIComponent(profile.id)}/models${revision ? "?refresh=true" : ""}`, { signal: controller.signal })
      .then(result => { if (!controller.signal.aborted) { setCatalog(result); setError(result.error || ""); } })
      .catch(e => { if (!controller.signal.aborted) setError(e.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [profile?.id, profile?.adapter, discoverable, revision]);
  const models = catalog?.models.filter(m => m.chatCapable) ?? [];
  return <div className="model-picker">
    {discoverable && <>
      <label>{label}
        <select value={value} disabled={disabled} onChange={e => onChange(e.target.value)}>
          <option value="">{defaultLabel}</option>
          {value && !models.some(m => m.id === value) && <option value={value}>{value} · saved or manual ID</option>}
          {models.map(m => <option value={m.id} key={m.id}>{m.name === m.id ? m.id : `${m.name} · ${m.id}`}</option>)}
        </select>
      </label>
      <div className="model-discovery-status">
        <small role="status">{loading ? "Detecting models…" : error || (models.length ? `${models.length} chat models detected` : "No chat models detected. You can enter an ID below.")}</small>
        <button type="button" disabled={disabled || loading} onClick={() => setRevision(n => n + 1)}>Refresh models</button>
      </div>
    </>}
    <label>{discoverable ? "Model ID (editable)" : label}
      <input value={value} maxLength={200} disabled={disabled} onChange={e => onChange(e.target.value)} placeholder={defaultLabel} />
    </label>
  </div>;
}

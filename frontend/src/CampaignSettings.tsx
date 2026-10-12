import { useEffect, useState } from "react";
import { api, type Campaign } from "./api";

export default function CampaignSettings({ campaign, busy, onSaved }: {
  campaign: Campaign;
  busy: boolean;
  onSaved: (campaign: Campaign) => void;
}) {
  const [rollModeEnabled, setRollModeEnabled] = useState(campaign.rollModeEnabled);
  const [generalInstructions, setGeneralInstructions] = useState(campaign.generalInstructions);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  useEffect(() => {
    setRollModeEnabled(campaign.rollModeEnabled);
    setGeneralInstructions(campaign.generalInstructions);
    setError("");
  }, [campaign.id, campaign.rollModeEnabled, campaign.generalInstructions]);
  const dirty = rollModeEnabled !== campaign.rollModeEnabled || generalInstructions !== campaign.generalInstructions;
  return <details className="panel campaign-settings">
    <summary>Campaign settings</summary>
    <form onSubmit={async e => {
      e.preventDefault(); setSaving(true); setError(""); setSaved(false);
      try {
        const updated = await api<Campaign>(`/campaigns/${campaign.id}/settings`, {
          method: "PUT", body: JSON.stringify({ rollModeEnabled, generalInstructions }),
        });
        onSaved({ ...campaign, ...updated }); setSaved(true);
      } catch (e) { setError(e instanceof Error ? e.message : "Could not save campaign settings."); }
      finally { setSaving(false); }
    }}>
      <label className="campaign-roll-toggle">
        <input type="checkbox" role="switch" checked={rollModeEnabled} disabled={busy || saving}
          onChange={e => { setRollModeEnabled(e.target.checked); setSaved(false); }} />
        Roll mode {rollModeEnabled ? "on" : "off"}
      </label>
      <p>Use d20 checks for uncertain actions in Autonomous mode. Turn this off to resolve actions narratively.</p>
      <label>General instructions
        <textarea rows={5} maxLength={8000} value={generalInstructions} disabled={busy || saving}
          placeholder="For example: Accommodate the player's intent, keep the story coherent, and enhance each scene."
          onChange={e => { setGeneralInstructions(e.target.value); setSaved(false); }} />
      </label>
      <p>{generalInstructions.length.toLocaleString()} / 8,000 characters. These instructions take priority over world author guidance and creative defaults. Use the toggle to control rolls.</p>
      <p>Applies to all timelines in this campaign and to new turns. Retrying a saved turn keeps its original settings.</p>
      {error && <p className="text-error" role="alert">{error}</p>}
      {saved && <p role="status">Campaign settings saved.</p>}
      <button type="submit" disabled={busy || saving || !dirty}>{saving ? "Saving…" : "Save campaign settings"}</button>
    </form>
  </details>;
}

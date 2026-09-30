import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Activity,
  Brain,
  GitBranch,
  Settings,
  Shield,
  ZoomIn,
  ZoomOut,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { api, post } from "./api";
import type { Profiles, Workspace } from "./api";
import "./engine.css";
import ModelPicker from "./ModelPicker";

type Entity = {
  id: string;
  name: string;
  kind: string;
  locationId?: string;
  description: string;
};
type Occurrence = {
  id: string;
  order: number;
  beat: number;
  actorId: string;
  locationId: string;
  text: string;
  perceivedBy: string[];
  entityIds: string[];
  time: string;
  kind: string;
};
type Memory = {
  id: string;
  ownerId: string;
  term: "short" | "long";
  kind: string;
  text: string;
  pinned: boolean;
  evidenceEventIds: string[];
};
type Character = {
  entityId: string;
  goal: string;
  belief: string;
  emotion: string;
  intention: string;
  decisionSummary: string;
  stats: Record<string, number>;
};
type Check = {
  id: string;
  actorId: string;
  attempt: string;
  difficulty: number;
  modifiers: { label: string; value: number }[];
  roll: number;
  total: number;
  success: boolean;
  explanation: string;
};
type SettingsValue = {
  characterModel?: string | null;
  directorModel?: string | null;
  characterProfile: string;
  directorProfile: string;
  maxBeats: number;
  maxNpcs: number;
};
type World = {
  entities: Entity[];
  characters: Character[];
  events: Occurrence[];
  connections: {
    id: string;
    sourceId: string;
    targetId: string;
    kind: string;
    evidenceEventId: string;
  }[];
  memories: Memory[];
  checks: Check[];
  settings: SettingsValue;
  timeMinutes: number;
  playerLocationId: string;
};
type Run = {
  id: string;
  status: string;
  error?: string;
  action: string;
  draft?: {
    world: World;
    steps: { key: string; task: string; completedAt: string }[];
  };
};
type Engine = { checkpointId: string; world: World; runs: Run[] };
type Tab = "live" | "graph" | "memory" | "author";
const kinds = ["character", "object", "being", "place"];
const supportsAgent = (p: Profiles["profiles"][number]) => p.agentSupported ?? ["fixture", "openai", "openai-compatible", "ollama", "lemonade"].includes(p.adapter);
const colors: Record<string, string> = {
  character: "#668750",
  object: "#aa874d",
  being: "#826e9e",
  place: "#4e8990",
};

export default function StoryEngine({
  workspace,
  busy,
  onRefresh,
}: {
  workspace: Workspace;
  busy: boolean;
  onRefresh: () => Promise<void>;
}) {
  const [data, setData] = useState<Engine | null>(null);
  const [checkpoint, setCheckpoint] = useState("");
  const [tab, setTab] = useState<Tab>("live");
  const [spoilers, setSpoilers] = useState(false);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);
  const [profiles, setProfiles] = useState<Profiles | null>(null);
  const [settings, setSettings] = useState<SettingsValue | null>(null);
  const [owner, setOwner] = useState("player");
  const [editing, setEditing] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [selected, setSelected] = useState("");
  const [filters, setFilters] = useState(kinds);
  const [zoom, setZoom] = useState(1);
  const request = useRef(0);
  const branch = workspace.branch.id;
  const historical = !!checkpoint && checkpoint !== workspace.checkpoint.id;
  const refresh = useCallback(async () => {
    const current = ++request.current;
    try {
      const value = await api<Engine>(
        `/branches/${branch}/engine${checkpoint ? `?checkpointId=${encodeURIComponent(checkpoint)}` : ""}`,
      );
      if (current === request.current) {
        setData(value);
        setError("");
      }
    } catch (e) {
      if (current === request.current) setError((e as Error).message);
    }
  }, [branch, checkpoint]);
  useEffect(() => {
    setData(null);
    setSelected("");
    setEditing(null);
    setSettings(null);
    void refresh();
    return () => {
      ++request.current;
    };
  }, [refresh, workspace.checkpoint.id]);
  useEffect(() => {
    api<Profiles>("/providers")
      .then(setProfiles)
      .catch(() => {});
  }, []);
  const pending = data?.runs.some((r) => r.status === "running") ?? false;
  useEffect(() => {
    if (historical || (!busy && !pending && !saving)) return;
    const timer = setInterval(() => {
      void refresh();
    }, 1500);
    return () => clearInterval(timer);
  }, [busy, pending, saving, historical, refresh]);
  useEffect(() => {
    if (!spoilers) {
      setOwner("player");
      setTab((t) => (t === "author" ? "live" : t));
      setEditing(null);
    }
  }, [spoilers]);
  const world = data?.world;
  const activeRun = !historical
    ? data?.runs.find((r) => ["running", "paused"].includes(r.status))
    : undefined;
  const liveWorld = activeRun?.draft?.world ?? world;
  const events = useMemo(
    () =>
      (liveWorld?.events ?? [])
        .filter((e) => spoilers || e.perceivedBy.includes("player"))
        .sort((a, b) => a.order - b.order),
    [liveWorld, spoilers],
  );
  const committedEvents = useMemo(
    () =>
      (world?.events ?? []).filter(
        (e) => spoilers || e.perceivedBy.includes("player"),
      ),
    [world, spoilers],
  );
  const known = useMemo(
    () =>
      new Set([
        "player",
        world?.playerLocationId ?? "",
        ...committedEvents.flatMap((e) => [
          e.actorId,
          e.locationId,
          ...e.entityIds,
        ]),
      ]),
    [world, committedEvents],
  );
  const entities = (world?.entities ?? []).filter(
    (e) => (spoilers || known.has(e.id)) && filters.includes(e.kind),
  );
  const entityIds = new Set(entities.map((e) => e.id));
  const visibleEventIds = new Set(committedEvents.map((e) => e.id));
  const connections = (world?.connections ?? []).filter(
    (c) =>
      entityIds.has(c.sourceId) &&
      entityIds.has(c.targetId) &&
      (spoilers || visibleEventIds.has(c.evidenceEventId)),
  );
  const named = (id: string) =>
    id === "director"
      ? "Director"
      : id === "player"
        ? "Player"
        : (world?.entities.find((e) => e.id === id)?.name ?? id);
  const readOnly = historical || busy || pending || saving || !!activeRun;
  const perform = async (task: () => Promise<unknown>) => {
    setSaving(true);
    setError("");
    try {
      await task();
      setEditing(null);
      await onRefresh();
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  };
  const updateMemory = (
    m: Memory,
    patch: { text?: string; pinned?: boolean; remove?: boolean },
  ) =>
    perform(() =>
      post(`/branches/${branch}/memories`, {
        expectedCheckpointId: workspace.checkpoint.id,
        id: m.id,
        text: m.text,
        pinned: m.pinned,
        ...patch,
      }),
    );
  const inspectOccurrence = (id: string) => {
    setTab("live");
    setTimeout(
      () =>
        document
          .getElementById(`occurrence-${id}`)
          ?.scrollIntoView({ behavior: "smooth", block: "center" }),
      0,
    );
  };
  const positions = new Map(
    entities.map((entity, index) => {
      const angle =
        (2 * Math.PI * index) / Math.max(entities.length, 1) - Math.PI / 2;
      return [
        entity.id,
        { x: 350 + 250 * Math.cos(angle), y: 255 + 175 * Math.sin(angle) },
      ];
    }),
  );
  const tabs: [Tab, LucideIcon, string][] = [
    ["live", Activity, "Live room"],
    ["graph", GitBranch, "World graph"],
    ["memory", Brain, "Memory"],
  ];
  if (spoilers) tabs.push(["author", Settings, "Director & characters"]);

  return (
    <section className="engine-page">
      <div className="engine-heading">
        <div>
          <div className="engine-eyebrow">AUTONOMOUS STORYTELLER</div>
          <h1>Live world</h1>
          <p>
            {workspace.campaign.name} · {workspace.branch.name}
          </p>
        </div>
        <label className="engine-spoilers">
          <input
            type="checkbox"
            checked={spoilers}
            onChange={(e) => setSpoilers(e.target.checked)}
          />
          <Shield size={16} /> Author view · reveals secrets
        </label>
      </div>
      <div className="engine-toolbar">
        <label>
          Checkpoint{" "}
          <select
            value={checkpoint}
            onChange={(e) => {
              setCheckpoint(e.target.value);
              setSettings(null);
            }}
          >
            <option value="">Latest committed world</option>
            {workspace.checkpoints.map((c) => (
              <option key={c.id} value={c.id}>
                {c.sequence} · {c.label}
              </option>
            ))}
          </select>
        </label>
        <span>
          {historical
            ? "Historical snapshot · read only"
            : `${world?.timeMinutes ?? 0} story minutes`}
        </span>
        <button onClick={() => void refresh()} disabled={saving}>
          Refresh
        </button>
      </div>
      {error && (
        <div role="alert" className="engine-error">
          {error}
        </div>
      )}
      <nav className="engine-tabs" aria-label="World sections">
        {tabs.map(([id, Icon, label]) => (
          <button
            key={id}
            className={tab === id ? "active" : ""}
            aria-current={tab === id ? "page" : undefined}
            onClick={() => setTab(id)}
          >
            <Icon size={17} />
            {label}
          </button>
        ))}
      </nav>
      {!data && !error && <p role="status">Loading world…</p>}
      {data && tab === "live" && (
        <>
          {data.runs.length > 0 && !historical && (
            <div className="engine-runs">
              {data.runs.slice(0, 5).map((r) => (
                <article key={r.id} className={`engine-run ${r.status}`}>
                  <div>
                    <strong>
                      {r.status === "running"
                        ? "Characters are responding"
                        : r.status === "paused"
                          ? "Generation paused"
                          : `Generation ${r.status}`}
                    </strong>
                    <p>{r.action}</p>
                    {r.error && <small>{r.error}</small>}
                    {spoilers && r.draft && (
                      <small>
                        {r.draft.steps.length} saved generation steps ·{" "}
                        {r.draft.steps.at(-1)?.task}
                      </small>
                    )}
                  </div>
                  <div>
                    {r.status === "paused" && (
                      <button
                        disabled={saving || busy}
                        onClick={() =>
                          void perform(() => post(`/runs/${r.id}/retry`))
                        }
                      >
                        Retry
                      </button>
                    )}
                    {["running", "paused"].includes(r.status) && (
                      <button
                        onClick={() =>
                          void perform(() => post(`/runs/${r.id}/cancel`))
                        }
                      >
                        Cancel
                      </button>
                    )}
                  </div>
                </article>
              ))}
            </div>
          )}
          <div className="engine-section-heading">
            <h2>What is happening</h2>
            <span>
              {spoilers
                ? "All events · includes off-screen activity"
                : "Events the player can perceive"}
            </span>
          </div>
          <ol className="engine-events" aria-label="Chronological occurrences">
            {events.map((e) => (
              <li id={`occurrence-${e.id}`} key={e.id} tabIndex={-1}>
                <div className="engine-event-order">{e.order}</div>
                <article>
                  <div>
                    <strong>{named(e.actorId)}</strong>
                    <small>
                      {named(e.locationId)} · beat {e.beat} ·{" "}
                      {String(e.time ?? "")}
                    </small>
                  </div>
                  <p>{e.text}</p>
                  <span className="engine-badge">{e.kind}</span>
                  {spoilers && (
                    <small>
                      Perceived by:{" "}
                      {e.perceivedBy.map(named).join(", ") || "Nobody"}
                    </small>
                  )}
                </article>
              </li>
            ))}
          </ol>
          {events.length === 0 && (
            <p className="engine-empty">
              The room is quiet. Take an action in Continue story to start a
              reaction.
            </p>
          )}
          <p className="engine-footnote">
            Character actions are attempts until the director resolves them.
            Control returns to you when a decision is needed.
          </p>
        </>
      )}
      {data && tab === "graph" && (
        <>
          <div className="engine-section-heading">
            <h2>Connections & occurrences</h2>
            <span>
              {spoilers ? "Complete world" : "Player knowledge"} ·{" "}
              {entities.length} entities
            </span>
          </div>
          <div className="engine-graph-controls">
            {kinds.map((kind) => (
              <label key={kind}>
                <input
                  type="checkbox"
                  checked={filters.includes(kind)}
                  onChange={(e) =>
                    setFilters((f) =>
                      e.target.checked
                        ? [...f, kind]
                        : f.filter((k) => k !== kind),
                    )
                  }
                />
                <span style={{ background: colors[kind] }} />
                {kind}
              </label>
            ))}
            <button
              aria-label="Zoom out"
              onClick={() => setZoom((z) => Math.max(0.6, z - 0.2))}
            >
              <ZoomOut size={17} />
            </button>
            <button
              aria-label="Zoom in"
              onClick={() => setZoom((z) => Math.min(2, z + 0.2))}
            >
              <ZoomIn size={17} />
            </button>
            <button
              onClick={() => {
                setZoom(1);
                setFilters(kinds);
              }}
            >
              Reset
            </button>
          </div>
          <div className="engine-graph-layout">
            <div className="engine-graph-scroll">
              <svg
                viewBox={`${350 - 350 / zoom} ${255 - 255 / zoom} ${700 / zoom} ${510 / zoom}`}
                role="img"
                aria-label="Interactive graph of world entities. Select a named entity to inspect its relationships."
              >
                <g>
                  {connections.map((c) => {
                    const a = positions.get(c.sourceId)!;
                    const b = positions.get(c.targetId)!;
                    return (
                      <g key={c.id}>
                        <line
                          x1={a.x}
                          y1={a.y}
                          x2={b.x}
                          y2={b.y}
                          stroke="#c4cec0"
                          strokeWidth={
                            selected === c.sourceId || selected === c.targetId
                              ? 3
                              : 1.5
                          }
                        />
                        <text
                          x={(a.x + b.x) / 2}
                          y={(a.y + b.y) / 2 - 5}
                          textAnchor="middle"
                          className="engine-edge-label"
                        >
                          {c.kind}
                        </text>
                      </g>
                    );
                  })}
                  {entities.map((entity) => {
                    const p = positions.get(entity.id)!;
                    return (
                      <g
                        key={entity.id}
                        transform={`translate(${p.x} ${p.y})`}
                        role="button"
                        aria-label={`Inspect ${entity.name}, ${entity.kind}`}
                        tabIndex={0}
                        onClick={() => setSelected(entity.id)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter" || e.key === " ") {
                            e.preventDefault();
                            setSelected(entity.id);
                          }
                        }}
                        className="engine-node"
                      >
                        <circle
                          r={selected === entity.id ? 28 : 22}
                          fill={colors[entity.kind] ?? "#668750"}
                          stroke={selected === entity.id ? "#27362e" : "white"}
                          strokeWidth={3}
                        />
                        <text
                          y={4}
                          textAnchor="middle"
                          fill="white"
                          fontSize={12}
                        >
                          {entity.kind.slice(0, 1).toUpperCase()}
                        </text>
                        <text
                          y={44}
                          textAnchor="middle"
                          className="engine-node-label"
                        >
                          {entity.name}
                        </text>
                      </g>
                    );
                  })}
                </g>
              </svg>
              {entities.length === 0 && (
                <p className="engine-empty">
                  No known entities match these filters.
                </p>
              )}
            </div>
            <aside className="engine-inspector">
              {selected && entityIds.has(selected) ? (
                <>
                  <h3>{named(selected)}</h3>
                  <p>
                    {
                      world?.entities.find((e) => e.id === selected)
                        ?.description
                    }
                  </p>
                  <h4>Connections</h4>
                  {connections
                    .filter(
                      (c) => c.sourceId === selected || c.targetId === selected,
                    )
                    .map((c) => (
                      <div className="engine-relation" key={c.id}>
                        <strong>
                          {named(c.sourceId)} → {c.kind} → {named(c.targetId)}
                        </strong>
                        {visibleEventIds.has(c.evidenceEventId) && (
                          <button
                            onClick={() => inspectOccurrence(c.evidenceEventId)}
                          >
                            View supporting occurrence
                          </button>
                        )}
                      </div>
                    ))}
                  <h4>Occurrence history</h4>
                  {committedEvents
                    .filter(
                      (e) =>
                        e.actorId === selected ||
                        e.locationId === selected ||
                        e.entityIds.includes(selected),
                    )
                    .sort((a, b) => a.order - b.order)
                    .map((e) => (
                      <button
                        className="engine-occurrence-link"
                        key={e.id}
                        onClick={() => inspectOccurrence(e.id)}
                      >
                        #{e.order} · {e.text}
                      </button>
                    ))}
                </>
              ) : (
                <>
                  <h3>Explore the world</h3>
                  <p>
                    Select an entity to inspect its connections and supporting
                    events. Use the filters to focus the graph.
                  </p>
                </>
              )}
            </aside>
          </div>
        </>
      )}
      {data && tab === "memory" && (
        <>
          <div className="engine-section-heading">
            <h2>Memory</h2>
            <label>
              Owner{" "}
              <select
                value={owner}
                onChange={(e) => {
                  setOwner(e.target.value);
                  setEditing(null);
                }}
              >
                <option value="player">Player</option>
                {spoilers && (
                  <>
                    <option value="director">Director</option>
                    {world?.characters
                      .filter((c) => c.entityId !== "player")
                      .map((c) => (
                        <option key={c.entityId} value={c.entityId}>
                          {named(c.entityId)}
                        </option>
                      ))}
                  </>
                )}
              </select>
            </label>
          </div>
          <p>
            Facts represent established knowledge. Beliefs and rumors can be
            mistaken. Editing a belief changes future context without rewriting
            history.
          </p>
          {historical && (
            <p className="engine-footnote">
              Choose the latest checkpoint to edit memory.
            </p>
          )}
          <div className="engine-memory-columns">
            {(["short", "long"] as const).map((term) => (
              <section key={term}>
                <h3>{term === "short" ? "Short-term" : "Long-term"} memory</h3>
                {world?.memories
                  .filter(
                    (m) =>
                      m.ownerId === owner &&
                      m.term === term &&
                      (spoilers || owner === "player"),
                  )
                  .map((m) => (
                    <article className="engine-memory" key={m.id}>
                      <div>
                        <span className="engine-badge">{m.kind}</span>
                        {m.pinned && (
                          <span className="engine-badge">Pinned</span>
                        )}
                      </div>
                      {editing === m.id ? (
                        <>
                          <label
                            className="engine-sr-only"
                            htmlFor={`memory-${m.id}`}
                          >
                            Memory text
                          </label>
                          <textarea
                            id={`memory-${m.id}`}
                            value={text}
                            onChange={(e) => setText(e.target.value)}
                            rows={4}
                            maxLength={1000}
                          />
                          <div className="engine-actions">
                            <button
                              disabled={readOnly || !text.trim()}
                              onClick={() =>
                                void updateMemory(m, { text: text.trim() })
                              }
                            >
                              Save correction
                            </button>
                            <button onClick={() => setEditing(null)}>
                              Discard
                            </button>
                          </div>
                        </>
                      ) : (
                        <>
                          <p>{m.text}</p>
                          <div className="engine-actions">
                            <button
                              disabled={readOnly}
                              onClick={() => {
                                setEditing(m.id);
                                setText(m.text);
                              }}
                            >
                              Edit
                            </button>
                            <button
                              disabled={readOnly}
                              onClick={() =>
                                void updateMemory(m, { pinned: !m.pinned })
                              }
                            >
                              {m.pinned ? "Unpin" : "Pin"}
                            </button>
                            <button
                              disabled={readOnly}
                              onClick={() =>
                                void updateMemory(m, { remove: true })
                              }
                            >
                              Remove
                            </button>
                          </div>
                        </>
                      )}
                      {m.evidenceEventIds
                        .filter((id) => visibleEventIds.has(id))
                        .map((id) => (
                          <button
                            key={id}
                            className="engine-evidence"
                            onClick={() => inspectOccurrence(id)}
                          >
                            Supporting occurrence
                          </button>
                        ))}
                    </article>
                  ))}
                {!world?.memories.some(
                  (m) => m.ownerId === owner && m.term === term,
                ) && (
                  <p className="engine-empty">No {term}-term memories yet.</p>
                )}
              </section>
            ))}
          </div>
        </>
      )}
      {data && tab === "author" && spoilers && (
        <>
          <div className="engine-section-heading">
            <h2>Director configuration</h2>
            <span>Director discretion · recorded precedents</span>
          </div>
          <form
            className="engine-settings"
            onSubmit={(e) => {
              e.preventDefault();
              if (settings)
                void perform(async () => {
                  await post(`/branches/${branch}/engine/settings`, {
                    expectedCheckpointId: workspace.checkpoint.id,
                    settings,
                  });
                  setSettings(null);
                });
            }}
          >
            {(
              [
                ["characterProfile", "Character model"],
                ["directorProfile", "Director model"],
              ] as const
            ).map(([key, label]) => (
              <div key={key}>
              <label>
                {label}
                <select
                  disabled={readOnly}
                  value={(settings ?? world!.settings)[key]}
                  onChange={(e) =>
                    setSettings((s) => ({
                      ...(s ?? world!.settings),
                      [key]: e.target.value,
                      [key === "characterProfile" ? "characterModel" : "directorModel"]: null,
                    }))
                  }
                >
                  {!profiles?.profiles.some(
                    (p) => p.id === (settings ?? world!.settings)[key],
                  ) && (
                    <option value={(settings ?? world!.settings)[key]}>
                      {(settings ?? world!.settings)[key]}
                    </option>
                  )}
                  {profiles?.profiles.map((p) => (
                    <option
                      key={p.id}
                      value={p.id}
                      disabled={
                        !p.enabled ||
                        !(p.adapterConfigured ?? p.configured) || !supportsAgent(p)
                      }
                    >
                      {p.name} · {p.model}
                      {!p.configured ? " (not configured)" : ""}
                      {!supportsAgent(p)
                        ? " (narrator only)"
                        : ""}
                    </option>
                  ))}
                </select>
              </label>
              <ModelPicker
                profile={profiles?.profiles.find(p => p.id === (settings ?? world!.settings)[key])}
                value={(settings ?? world!.settings)[key === "characterProfile" ? "characterModel" : "directorModel"] || ""}
                label={`${label} selection`}
                defaultLabel="Use connector default model"
                disabled={readOnly}
                onChange={model => setSettings(s => ({ ...(s ?? world!.settings), [key === "characterProfile" ? "characterModel" : "directorModel"]: model }))}
              />
              </div>
            ))}
            {(
              [
                ["maxNpcs", "Maximum NPCs", 10],
                ["maxBeats", "Maximum reaction beats", 5],
              ] as const
            ).map(([key, label, max]) => (
              <label key={key}>
                {label}
                <input
                  type="number"
                  min={1}
                  max={max}
                  required
                  disabled={readOnly}
                  value={(settings ?? world!.settings)[key]}
                  onChange={(e) =>
                    setSettings((s) => ({
                      ...(s ?? world!.settings),
                      [key]: Number(e.target.value),
                    }))
                  }
                />
              </label>
            ))}
            <button disabled={readOnly || !settings} type="submit">
              Save settings
            </button>
          </form>
          <h2>Character workspaces</h2>
          <p>
            Goals, beliefs, emotions and concise decision summaries guide
            independent characters.
          </p>
          <div className="engine-character-grid">
            {(liveWorld?.characters ?? []).map((c) => (
              <article key={c.entityId} className="engine-character">
                <h3>{named(c.entityId)}</h3>
                <dl>
                  {[
                    ["Goal", c.goal],
                    ["Belief", c.belief],
                    ["Emotion", c.emotion],
                    ["Intention", c.intention],
                    ["Decision summary", c.decisionSummary],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <dt>{label}</dt>
                      <dd>{value || "—"}</dd>
                    </div>
                  ))}
                </dl>
                <div>
                  {Object.entries(c.stats).map(([stat, value]) => (
                    <span key={stat} className="engine-badge">
                      {stat} {value >= 0 ? "+" : ""}
                      {value}
                    </span>
                  ))}
                </div>
              </article>
            ))}
          </div>
          <h2>Checks & rulings</h2>
          <p>
            Modifiers and difficulty are fixed before the server rolls a d20.
            Natural 1 and 20 add narrative flavor; totals decide success.
          </p>
          {(liveWorld?.checks ?? []).map((c) => (
            <article key={c.id} className="engine-check">
              <div>
                <strong>
                  {named(c.actorId)} · {c.attempt}
                </strong>
                <span
                  className={`engine-badge ${c.success ? "success" : "failure"}`}
                >
                  {c.success ? "Success" : "Failure"}
                </span>
              </div>
              <p>
                d20 {c.roll}
                {c.modifiers
                  .map(
                    (m) =>
                      ` ${m.value >= 0 ? "+" : "−"} ${Math.abs(m.value)} (${m.label})`,
                  )
                  .join("")}{" "}
                = <strong>{c.total}</strong> against difficulty{" "}
                <strong>{c.difficulty}</strong>
              </p>
              <p>{c.explanation}</p>
            </article>
          ))}
          {!liveWorld?.checks.length && (
            <p className="engine-empty">No contested checks yet.</p>
          )}
        </>
      )}
    </section>
  );
}

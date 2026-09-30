import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  ArrowUp,
  BookOpen,
  Check,
  ChevronRight,
  Compass,
  Download,
  FileText,
  GitBranch,
  Layers,
  MapPin,
  Plus,
  Settings,
  Shield,
  Sparkles,
  Square,
  Upload,
  X,
} from "lucide-react";
import {
  api,
  downloadCampaignExport,
  downloadSource,
  post,
  streamTurn,
} from "./api";
import StoryEngine from "./StoryEngine";
import ModelPicker from "./ModelPicker";
import ImportReview from "./ImportReview";
import DeleteStoryModal from "./DeleteStoryModal";
import type {
  Branch,
  Campaign,
  Fact,
  Job,
  Profiles,
  Review,
  RetrievalHit,
  State,
  Thread,
  Workspace,
} from "./api";

type Page =
  "library" | "story" | "engine" | "imports" | "branches" | "providers";
type Decision = {
  factId: string;
  accept: boolean;
  text: string;
  visibility: string;
  kind: string;
  confidence: number;
  knownBy: string[];
};
type ReviewedThread = Thread & { accept: boolean };

export default function App() {
  const [page, setPage] = useState<Page>("library");
  const [campaigns, setCampaigns] = useState<Campaign[]>([]);
  const [workspace, setWorkspace] = useState<Workspace | null>(null);
  const [branchId, setBranchId] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [newCampaign, setNewCampaign] = useState(false);
  const [deleteStory, setDeleteStory] = useState<Campaign | null>(null);
  const [name, setName] = useState("");
  const [synthetic, setSynthetic] = useState(true);
  const [action, setAction] = useState("");
  const [draft, setDraft] = useState("");
  const [turnMode, setTurnMode] = useState<"autonomous" | "narrator">(
    "autonomous",
  );
  const [token, setToken] = useState(sessionStorage.getItem("occ-token") || "");
  const [profiles, setProfiles] = useState<Profiles | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [review, setReview] = useState<Review | null>(null);
  const [decisions, setDecisions] = useState<Decision[]>([]);
  const [threadDecisions, setThreadDecisions] = useState<ReviewedThread[]>([]);
  const [resumeState, setResumeState] = useState("");
  const [forkName, setForkName] = useState("An alternate path");
  const [forkCheckpoint, setForkCheckpoint] = useState("");
  const [forkAction, setForkAction] = useState("");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [importInputLimit, setImportInputLimit] = useState(24000);
  const [importOutputLimit, setImportOutputLimit] = useState(3000);
  const [importCallLimit, setImportCallLimit] = useState(100);
  const [importProfile, setImportProfile] = useState("");
  const [importModel, setImportModel] = useState("");
  const [selectedExportFile, setSelectedExportFile] = useState<File | null>(
    null,
  );
  const [editingFact, setEditingFact] = useState<string | null>(null);
  const [correctionText, setCorrectionText] = useState("");
  const [correctionReason, setCorrectionReason] = useState("");
  const [searchQuery, setSearchQuery] = useState("");
  const [searchHits, setSearchHits] = useState<RetrievalHit[]>([]);
  const [authorView, setAuthorView] = useState(false);
  const generation = useRef<AbortController | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const loadLibrary = useCallback(
    async () => setCampaigns(await api<Campaign[]>("/campaigns")),
    [],
  );
  const loadWorkspace = useCallback(async (id: string) => {
    const data = await api<Workspace>(`/branches/${id}`);
    setWorkspace(data);
    setForkCheckpoint(data.checkpoint.id);
  }, []);
  const run = async (task: () => Promise<void>) => {
    setError("");
    setNotice("");
    setBusy(true);
    try {
      await task();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Something went wrong.");
    } finally {
      setBusy(false);
    }
  };
  useEffect(() => {
    loadLibrary().catch((e) => setError(e.message));
  }, [loadLibrary]);
  useEffect(() => {
    if (page === "imports") void api<Profiles>("/providers").then(setProfiles).catch(e => setError(e.message));
  }, [page]);
  useEffect(() => {
    if (page === "story") end.current?.scrollIntoView({ behavior: "smooth" });
  }, [workspace?.messages.length, page]);
  useEffect(() => {
    if (page !== "imports" || !branchId) return;
    let active = true;
    const refresh = async () => {
      try {
        const rows = await api<Job[]>(`/branches/${branchId}/imports`);
        if (active) setJobs(rows);
      } catch (e) {
        if (active) setError((e as Error).message);
      }
    };
    void refresh();
    const timer = setInterval(refresh, 2000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [page, branchId]);

  const openBranch = (id: string) =>
    run(async () => {
      await loadWorkspace(id);
      setBranchId(id);
      setReview(null);
      setAction("");
      setAuthorView(false);
      setPage("story");
    });
  const navigate = (next: Page) => {
    if (busy && next !== "story" && next !== "engine") return;
    setPage(next);
    setError("");
    setNotice("");
    if (next === "providers")
      void run(async () => setProfiles(await api<Profiles>("/providers")));
    if (next === "library") void run(loadLibrary);
  };
  const create = () =>
    run(async () => {
      const branch = await post<Branch>("/campaigns", { name, synthetic });
      await loadWorkspace(branch.id);
      setBranchId(branch.id);
      setPage("story");
      setNewCampaign(false);
      setName("");
      await loadLibrary();
    });
  const importCampaignExport = () =>
    run(async () => {
      if (!selectedExportFile) return;
      const portable = JSON.parse(await selectedExportFile.text());
      const branch = await post<Branch>("/campaigns/import", {
        export: portable,
      });
      await loadLibrary();
      await loadWorkspace(branch.id);
      setBranchId(branch.id);
      setPage("story");
      setSelectedExportFile(null);
      setNotice("Campaign export restored as a new independent copy.");
    });
  const correctFact = (fact: Fact) =>
    run(async () => {
      if (!workspace || !correctionText.trim() || !correctionReason.trim())
        return;
      await post(`/branches/${workspace.branch.id}/facts/${fact.id}/correct`, {
        expectedCheckpointId: workspace.checkpoint.id,
        text: correctionText,
        reason: correctionReason,
        visibility: fact.visibility,
        kind: fact.kind,
        confidence: fact.confidence,
        knownBy: JSON.parse(fact.knownByJson || "[]"),
      });
      await loadWorkspace(workspace.branch.id);
      setEditingFact(null);
      setCorrectionText("");
      setCorrectionReason("");
      setNotice("Canon correction saved with an audit record.");
    });
  const searchMemory = () =>
    run(async () => {
      if (!workspace || !searchQuery.trim()) return;
      setSearchHits(
        await api<RetrievalHit[]>(
          `/branches/${workspace.branch.id}/search?q=${encodeURIComponent(searchQuery)}`,
        ),
      );
    });
  const regenerateLastTurn = () =>
    run(async () => {
      if (!workspace) return;
      const current = workspace.checkpoints.find(
        (c) => c.id === workspace.checkpoint.id,
      );
      const previous =
        current &&
        workspace.checkpoints.find((c) => c.sequence === current.sequence - 2);
      const lastAction =
        [...workspace.messages].reverse().find((m) => m.role === "user")
          ?.content || "";
      if (!previous)
        throw new Error("There is no earlier checkpoint to regenerate from.");
      const branch = await post<Branch>(
        `/branches/${workspace.branch.id}/fork`,
        {
          checkpointId: previous.id,
          name: `Regenerated · ${new Date().toLocaleTimeString()}`,
        },
      );
      await loadWorkspace(branch.id);
      setBranchId(branch.id);
      setAction(lastAction);
      setPage("story");
      setNotice(
        `A new branch is ready in ${turnMode === "autonomous" ? "autonomous director" : "narrator streaming"} mode. Edit the action if needed, then continue.`,
      );
    });
  const send = () =>
    run(async () => {
      if (!workspace) return;
      const controller = new AbortController();
      generation.current = controller;
      try {
        setDraft("");
        if (turnMode === "narrator") {
          await streamTurn(
            `/branches/${workspace.branch.id}/turns/stream`,
            { action, expectedCheckpointId: workspace.checkpoint.id },
            (text) => setDraft((old) => old + text),
            controller.signal,
          );
        } else {
          await post(
            `/branches/${workspace.branch.id}/turns`,
            { action, expectedCheckpointId: workspace.checkpoint.id },
            controller.signal,
          );
        }
        setAction("");
      } catch (e) {
        if ((e as Error).name !== "AbortError") throw e;
        setNotice("Stopped. Reloading the last committed checkpoint.");
      } finally {
        generation.current = null;
        await loadWorkspace(workspace.branch.id);
        setDraft("");
      }
    });
  const inspect = (id: string) =>
    run(async () => {
      const data = await api<Review>(`/imports/${id}`);
      data.candidates.sort(
        (a, b) => (a.evidence[0]?.ordinal ?? 0) - (b.evidence[0]?.ordinal ?? 0),
      );
      setReview(data);
      setDecisions(
        data.candidates.map((c) => ({
          factId: c.fact.id,
          accept: c.fact.reviewStatus === "accepted",
          text: c.fact.text,
          visibility: c.fact.visibility,
          kind: c.fact.kind,
          confidence: c.fact.confidence,
          knownBy: JSON.parse(c.fact.knownByJson || "[]"),
        })),
      );
      setResumeState(
        data.job.proposedStateJson
          ? JSON.stringify(JSON.parse(data.job.proposedStateJson), null, 2)
          : "",
      );
      setThreadDecisions(
        JSON.parse(data.job.proposedThreadsJson || "[]").map(
          (thread: Thread) => ({ ...thread, accept: true }),
        ),
      );
    });
  const approve = () =>
    run(async () => {
      if (!review || !workspace) return;
      let state: State;
      try {
        state = JSON.parse(resumeState);
      } catch {
        throw new Error("The resume state must be valid JSON.");
      }
      await post(`/imports/${review.job.id}/approve`, {
        expectedCheckpointId: workspace.checkpoint.id,
        state,
        decisions,
        threads: threadDecisions,
      });
      await loadWorkspace(workspace.branch.id);
      setReview(null);
      setPage("story");
      setNotice(
        "Import approved. Your checkpoint and reviewed facts are saved.",
      );
    });

  return (
    <div className="shell">
      <aside className="sidebar">
        <button
          className="brand"
          onClick={() => navigate("library")}
          aria-label="Open OCC library"
        >
          <span className="brand-mark">
            <BookOpen size={23} />
          </span>
          <span>
            OPEN OCC<small>A place for your stories</small>
          </span>
        </button>
        <div className="nav-label">YOUR WORKSPACE</div>
        <nav>
          <button
            className={page === "library" ? "active" : ""}
            onClick={() => navigate("library")}
          >
            <Layers size={18} /> Story library
          </button>
          <button
            className={page === "story" ? "active" : ""}
            disabled={!workspace}
            onClick={() => navigate("story")}
          >
            <BookOpen size={18} /> Continue story
          </button>
          <button
            className={page === "engine" ? "active" : ""}
            disabled={!workspace}
            onClick={() => navigate("engine")}
          >
            <Sparkles size={18} /> Live world
          </button>
          <button
            className={page === "imports" ? "active" : ""}
            disabled={!workspace}
            onClick={() => navigate("imports")}
          >
            <Upload size={18} /> Import & review{" "}
            {jobs.some((j) => j.status === "review") && (
              <span className="nav-dot" />
            )}
          </button>
          <button
            className={page === "branches" ? "active" : ""}
            disabled={!workspace}
            onClick={() => navigate("branches")}
          >
            <GitBranch size={18} /> Timelines
          </button>
        </nav>
        {workspace && (
          <div className="current-campaign">
            <span className="nav-label">OPEN CAMPAIGN</span>
            <strong>{workspace.campaign.name}</strong>
            <span>
              <span className="dot" /> {workspace.branch.name}
            </span>
          </div>
        )}
        <div className="sidebar-bottom">
          <div className="private-note">
            <Shield size={18} />
            <span>
              Your world. Your history.
              <small>Private, self-hosted workspace</small>
            </span>
          </div>
          <button
            className={page === "providers" ? "active" : ""}
            onClick={() => navigate("providers")}
          >
            <Settings size={18} /> Providers & settings
          </button>
          <span className="version">STORYTELLER / 0.2</span>
        </div>
      </aside>
      <main>
        <header className="topbar">
          <span>
            Workspace <ChevronRight size={13} />{" "}
            {
              {
                library: "Story library",
                story: "Story",
                engine: "Live world",
                imports: "Import & review",
                branches: "Timelines",
                providers: "Settings",
              }[page]
            }
          </span>
          <span className="local">
            <span className="dot" /> Private workspace
          </span>
        </header>
        {(error || notice) && (
          <div
            role={error ? "alert" : "status"}
            className={`banner ${error ? "error" : ""}`}
          >
            <span>{error || notice}</span>
            <button
              aria-label="Dismiss message"
              onClick={() => {
                setError("");
                setNotice("");
              }}
            >
              <X size={16} />
            </button>
          </div>
        )}
        {page === "engine" && workspace && (
          <StoryEngine
            workspace={workspace}
            busy={busy}
            onRefresh={() => loadWorkspace(workspace.branch.id)}
          />
        )}
        {page === "library" && (
          <section className="page library">
            <div className="eyebrow">EVERY WORLD STARTS WITH A WORD</div>
            <div className="page-heading">
              <div>
                <h1>Your story, remembered.</h1>
                <p>
                  Return to a familiar world. Or give a new one a beginning.
                </p>
              </div>
              <button className="primary" onClick={() => setNewCampaign(true)}>
                <Plus size={17} /> New campaign
              </button>
              <label className="light-button export-import">
                <Upload size={15} /> Restore export
                <input
                  type="file"
                  accept=".json"
                  hidden
                  onChange={(e) =>
                    setSelectedExportFile(e.target.files?.[0] ?? null)
                  }
                />
              </label>
              {selectedExportFile && (
                <button className="light-button" onClick={importCampaignExport}>
                  Restore {selectedExportFile.name}
                </button>
              )}
            </div>
            <div className="hero">
              <div>
                <span className="eyebrow">THE NEXT CHAPTER IS YOURS</span>
                <h2>
                  Some stories deserve
                  <br />
                  to keep going.
                </h2>
                <p>
                  Keep the people, promises, and possibilities.
                  <br />
                  Build a world that remembers where you left off.
                </p>
                <button
                  className="light-button"
                  onClick={() => {
                    setSynthetic(false);
                    setNewCampaign(true);
                  }}
                >
                  <Upload size={16} /> Bring your story{" "}
                  <ChevronRight size={16} />
                </button>
              </div>
              <div className="hero-art" aria-hidden="true">
                <div className="orbit orbit-one" />
                <div className="orbit orbit-two" />
                <div className="star">✦</div>
                <div className="book-shape">
                  <BookOpen size={100} strokeWidth={0.7} />
                </div>
                <span className="art-caption">A WORLD WORTH RETURNING TO</span>
              </div>
            </div>
            <div className="section-heading">
              <h2>
                Your campaigns{" "}
                <span>{campaigns.length.toString().padStart(2, "0")}</span>
              </h2>
              <span>Saved in your workspace</span>
            </div>
            <div className="campaign-grid">
              {campaigns.map((c, i) => (
                <div className="campaign-card-container" key={c.id}>
                <button
                  disabled={busy}
                  className="campaign-card"
                  onClick={() => void openBranch(c.branches[0].id)}
                >
                  <div className={`campaign-cover cover-${i % 3}`}>
                    <Compass size={56} strokeWidth={0.7} />
                    <span>YOUR LIVING WORLD</span>
                  </div>
                  <div className="campaign-card-body">
                    <span className="eyebrow">CAMPAIGN</span>
                    <h3>{c.name}</h3>
                    <p>
                      {c.branches.length}{" "}
                      {c.branches.length === 1 ? "timeline" : "timelines"} ·{" "}
                      {new Date(c.createdAt).toLocaleDateString()}
                    </p>
                    <div className="card-footer">
                      Open story <ArrowUp size={18} />
                    </div>
                  </div>
                </button>
                <button className="delete-story-button" disabled={busy} aria-label={`Delete story ${c.name}`} onClick={() => setDeleteStory(c)}>Delete story</button>
                </div>
              ))}
              <button
                className="new-card"
                onClick={() => {
                  setSynthetic(true);
                  setNewCampaign(true);
                }}
              >
                <span className="plus-circle">
                  <Plus size={25} />
                </span>
                <h3>A new beginning</h3>
                <p>
                  Start with Crownspire Academy
                  <br />
                  or an empty campaign.
                </p>
                <span>
                  Create a campaign <ChevronRight size={14} />
                </span>
              </button>
            </div>
            <div className="foundation-note">
              <Sparkles size={18} />
              <p>
                <strong>A world you can explore.</strong> Independent
                characters, a director, world graphs and memory. Fixture
                profiles need no key; live models are configurable. Imports use
                your review to establish canon.
              </p>
            </div>
          </section>
        )}
        {page === "story" && workspace && (
          <div className="story-layout">
            <section className="story-main">
              <div className="story-heading">
                <div>
                  <span className="eyebrow">{workspace.branch.name}</span>
                  <h1>{workspace.campaign.name}</h1>
                </div>
                <div className="story-heading-actions">
                  <button
                    className="light-button"
                    type="button"
                    disabled={busy}
                    onClick={() => void regenerateLastTurn()}
                  >
                    <GitBranch size={15} /> Regenerate
                  </button>
                  <button
                    className="light-button"
                    type="button"
                    onClick={() =>
                      void run(() =>
                        downloadCampaignExport(
                          workspace.campaign.id,
                          workspace.campaign.name,
                        ),
                      )
                    }
                  >
                    <Download size={15} /> Export
                  </button>
                  <span className="badge">
                    {turnMode === "autonomous"
                      ? "Director turns"
                      : "Narrator stream"}
                  </span>
                </div>
              </div>
              <div className="scene-location">
                <MapPin size={15} /> {workspace.state.location}
              </div>
              <div className="messages">
                {authorView &&
                  workspace.messages.some(
                    (m) => m.provider === "imported-source",
                  ) && (
                    <details className="panel imported-history">
                      <summary>
                        Preserved transcript history ·{" "}
                        {
                          workspace.messages.filter(
                            (m) => m.provider === "imported-source",
                          ).length
                        }{" "}
                        passages
                      </summary>
                      <p>
                        Original source passages, not newly generated narration.
                        Browse import evidence for citations and review
                        decisions.
                      </p>
                      {workspace.messages
                        .filter((m) => m.provider === "imported-source")
                        .map((m) => (
                          <article key={m.id} className={`message ${m.role}`}>
                            <div className="message-label">
                              SOURCE · {m.role}
                            </div>
                            <div className="prose">{m.content}</div>
                          </article>
                        ))}
                    </details>
                  )}
                {workspace.messages
                  .filter((m) => m.provider !== "imported-source")
                  .map((m) => (
                    <article key={m.id} className={`message ${m.role}`}>
                      <div className="message-label">
                        {m.role === "user"
                          ? "YOUR ACTION"
                          : m.role === "system"
                            ? "CAMPAIGN RECORD"
                            : "THE STORY"}
                        <span>{m.role === "assistant" ? m.provider : ""}</span>
                      </div>
                      <div className="prose">{m.content}</div>
                    </article>
                  ))}
                {draft && (
                  <article className="message assistant streaming">
                    <div className="message-label">
                      THE STORY <span>streaming</span>
                    </div>
                    <div className="prose">{draft}</div>
                  </article>
                )}
                <div ref={end} />
              </div>
              <form
                className="composer"
                onSubmit={(e) => {
                  e.preventDefault();
                  void send();
                }}
              >
                <label className="turn-mode">
                  Story mode
                  <select
                    value={turnMode}
                    disabled={busy}
                    onChange={(e) =>
                      setTurnMode(e.target.value as "autonomous" | "narrator")
                    }
                  >
                    <option value="autonomous">
                      Autonomous characters & director
                    </option>
                    <option value="narrator">Narrator only · streaming</option>
                  </select>
                </label>
                <textarea
                  aria-label="Your next action"
                  placeholder="What do you do next?"
                  value={action}
                  onChange={(e) => setAction(e.target.value)}
                  maxLength={4000}
                  disabled={busy}
                />
                <div>
                  <span>
                    {turnMode === "autonomous"
                      ? "Director-coordinated story · follow progress in Live world"
                      : "Free-form actions · streamed narration"}
                  </span>
                  {generation.current ? (
                    <button
                      type="button"
                      onClick={() => generation.current?.abort()}
                    >
                      <Square size={14} /> Stop
                    </button>
                  ) : (
                    <button
                      className="primary"
                      disabled={busy || !action.trim()}
                    >
                      <ArrowUp size={17} /> Continue
                    </button>
                  )}
                </div>
              </form>
            </section>
            <aside className="state-panel">
              <span className="eyebrow">AT THIS MOMENT</span>
              <h2>Current state</h2>
              <label className="check-label author-toggle">
                <input
                  type="checkbox"
                  checked={authorView}
                  onChange={(e) => {
                    setAuthorView(e.target.checked);
                    setEditingFact(null);
                    setSearchHits([]);
                  }}
                />
                Author tools · reveals secrets
              </label>
              <div className="state-section">
                <h3>SCENE</h3>
                <p>{workspace.state.location}</p>
                <small>{workspace.state.time}</small>
              </div>
              <div className="state-section">
                <h3>ON YOUR HORIZON</h3>
                <p>{workspace.state.objective}</p>
              </div>
              <div className="state-section">
                <h3>CONDITION</h3>
                <span className="condition">
                  <span className="dot" /> {workspace.state.condition}
                </span>
              </div>
              <div className="state-section">
                <h3>SKILLS</h3>
                {Object.entries(workspace.state.skills).map(([key, value]) => (
                  <div className="state-row" key={key}>
                    <span>{key}</span>
                    <strong>{value}</strong>
                  </div>
                ))}
              </div>
              <div className="state-section">
                <h3>INVENTORY</h3>
                {Object.entries(workspace.state.inventory).map(
                  ([key, value]) => (
                    <div className="state-row" key={key}>
                      <span>{key}</span>
                      <strong>× {value}</strong>
                    </div>
                  ),
                )}
              </div>
              <div className="state-section">
                <h3>IN THE SCENE</h3>
                {workspace.state.participants.length ? (
                  workspace.state.participants.map((p) => <p key={p}>{p}</p>)
                ) : (
                  <small>No participants established</small>
                )}
              </div>
              {authorView && workspace.world && (
                <div className="state-section">
                  <h3>WORLD FOUNDATION</h3>
                  <p>
                    {workspace.world.world.name} · v
                    {workspace.world.version.version}
                  </p>
                  <small>
                    {workspace.world.world.description ||
                      "Versioned world rules are active."}
                  </small>
                </div>
              )}
              {authorView && workspace.characters?.length ? (
                <div className="state-section">
                  <h3>CHARACTERS</h3>
                  {workspace.characters.slice(0, 8).map((character) => (
                    <div className="fact" key={character.id}>
                      <p>{character.name}</p>
                      <small>{character.description || character.status}</small>
                    </div>
                  ))}
                </div>
              ) : null}
              {authorView && (
                <div className="state-section">
                  <h3>ACTIVE THREADS</h3>
                  {workspace.threads.filter((t) => t.status === "active")
                    .length ? (
                    workspace.threads
                      .filter((t) => t.status === "active")
                      .map((t) => (
                        <div className="fact" key={t.id || t.title}>
                          <span className="badge">{t.kind}</span>
                          <p>{t.title}</p>
                          <small>{t.details}</small>
                        </div>
                      ))
                  ) : (
                    <small>
                      No active promises, deadlines, or open threads
                    </small>
                  )}
                </div>
              )}
              <div className="state-section">
                <h3>REVIEWED FACTS</h3>
                {workspace.facts.some(
                  (f) => authorView || f.visibility === "public",
                ) ? (
                  workspace.facts
                    .filter((f) => authorView || f.visibility === "public")
                    .map((f) => (
                      <div className="fact" key={f.id}>
                        <span className="badge">
                          {f.visibility === "narrator"
                            ? "Narrator only"
                            : "Public"}
                        </span>
                        <p>{f.text}</p>
                        {authorView &&
                          (editingFact === f.id ? (
                            <div className="fact-edit">
                              <textarea
                                value={correctionText}
                                onChange={(e) =>
                                  setCorrectionText(e.target.value)
                                }
                                placeholder="Corrected canon"
                                maxLength={1000}
                              />
                              <input
                                value={correctionReason}
                                onChange={(e) =>
                                  setCorrectionReason(e.target.value)
                                }
                                placeholder="Why is this correction needed?"
                                maxLength={1000}
                              />
                              <button
                                type="button"
                                disabled={
                                  busy ||
                                  !correctionText.trim() ||
                                  !correctionReason.trim()
                                }
                                onClick={() => void correctFact(f)}
                              >
                                Save correction
                              </button>
                              <button
                                type="button"
                                onClick={() => setEditingFact(null)}
                              >
                                Cancel
                              </button>
                            </div>
                          ) : (
                            <button
                              type="button"
                              onClick={() => {
                                setEditingFact(f.id);
                                setCorrectionText(f.text);
                              }}
                            >
                              Correct
                            </button>
                          ))}
                      </div>
                    ))
                ) : (
                  <small>No public reviewed facts yet</small>
                )}
              </div>
              {authorView && (
                <div className="state-section">
                  <h3>MEMORY SEARCH</h3>
                  <form
                    onSubmit={(e) => {
                      e.preventDefault();
                      void searchMemory();
                    }}
                  >
                    <input
                      value={searchQuery}
                      onChange={(e) => setSearchQuery(e.target.value)}
                      placeholder="Search facts, scenes, threads"
                    />
                  </form>
                  {searchHits.slice(0, 6).map((hit) => (
                    <div className="fact" key={`${hit.type}-${hit.id}`}>
                      <span className="badge">{hit.type}</span>
                      <small>{hit.text}</small>
                    </div>
                  ))}
                </div>
              )}
              {authorView && workspace.summary?.text && (
                <div className="state-section">
                  <h3>ROLLING SUMMARY</h3>
                  <small>{workspace.summary.text}</small>
                </div>
              )}
              {authorView && workspace.mechanics?.length ? (
                <div className="state-section">
                  <h3>MECHANICS LEDGER</h3>
                  {workspace.mechanics.slice(0, 6).map((entry) => (
                    <div className="state-row" key={entry.id}>
                      <span>{entry.subject}</span>
                      <strong>
                        {entry.delta > 0 ? `+${entry.delta}` : entry.delta}
                      </strong>
                    </div>
                  ))}
                </div>
              ) : null}
              <div className="checkpoint-saved">
                <Check size={16} /> Approved checkpoint saved
              </div>
            </aside>
          </div>
        )}
        {page === "branches" && workspace && (
          <section className="page narrow">
            <div className="eyebrow">EVERY POSSIBILITY HAS A PLACE</div>
            <h1>Your timelines</h1>
            <p>
              A new branch preserves the selected checkpoint and its earlier
              history. Your original continuation stays available.
            </p>
            <div className="panel">
              <h2>Choose a timeline</h2>
              {workspace.branches.map((b) => (
                <button
                  className="list-row"
                  disabled={busy}
                  key={b.id}
                  onClick={() => void openBranch(b.id)}
                >
                  <GitBranch size={18} />
                  <strong>{b.name}</strong>
                  {b.id === branchId ? (
                    <span className="badge">Current</span>
                  ) : (
                    <ChevronRight size={17} />
                  )}
                </button>
              ))}
            </div>
            <form
              className="panel"
              onSubmit={(e) => {
                e.preventDefault();
                void run(async () => {
                  const b = await post<Branch>(`/branches/${branchId}/fork`, {
                    checkpointId: forkCheckpoint,
                    name: forkName,
                  });
                  await loadWorkspace(b.id);
                  setBranchId(b.id);
                  setAction(forkAction);
                  setPage("story");
                });
              }}
            >
              <h2>Take another path</h2>
              <label>
                Start from checkpoint
                <select
                  value={forkCheckpoint}
                  onChange={(e) => setForkCheckpoint(e.target.value)}
                >
                  {workspace.checkpoints.map((c) => (
                    <option key={c.id} value={c.id}>
                      #{c.sequence} · {c.label} ·{" "}
                      {new Date(c.createdAt).toLocaleString()}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Timeline name
                <input
                  value={forkName}
                  maxLength={100}
                  onChange={(e) => setForkName(e.target.value)}
                  required
                />
              </label>
              <label>
                Edited action (optional)
                <textarea
                  value={forkAction}
                  maxLength={4000}
                  onChange={(e) => setForkAction(e.target.value)}
                  placeholder="Set this before continuing on the new branch"
                />
              </label>
              <button className="primary" disabled={busy}>
                <GitBranch size={16} /> Create branch
              </button>
            </form>
          </section>
        )}
        {page === "imports" && workspace && (
          <section className="page narrow">
            <div className="eyebrow">KEEP THE HISTORY. FIND YOUR PLACE.</div>
            <h1>Bring your story with you.</h1>
            <p>
              Preserve the original, inspect the evidence, and choose the scene
              to resume.
            </p>
            {!review ? (
              <>
                <form
                  className="upload-panel"
                  onSubmit={(e) => {
                    e.preventDefault();
                    void run(async () => {
                      if (!selectedFile) return;
                      const body = new FormData();
                      body.append("file", selectedFile);
                      body.append(
                        "inputCharacterLimit",
                        String(importInputLimit),
                      );
                      body.append(
                        "outputTokenLimit",
                        String(importOutputLimit),
                      );
                      body.append("maxCalls", String(importCallLimit));
                      if (importProfile) { body.append("provider", importProfile); body.append("model", importModel); }
                      const job = await api<Job>(
                        `/branches/${branchId}/imports`,
                        { method: "POST", body },
                      );
                      setJobs(
                        await api<Job[]>(`/branches/${branchId}/imports`),
                      );
                      setNotice(
                        `Source preserved. Import ${job.id.slice(0, 8)} is queued for processing.`,
                      );
                    });
                  }}
                >
                  <Upload size={30} strokeWidth={1.2} />
                  <h2>A whole history, a fresh beginning.</h2>
                  <p>
                    UTF-8 text, Markdown, HTML, DOCX, or documented JSON · Up to
                    2 MiB
                  </p>
                  <input
                    aria-label="Transcript file"
                    type="file"
                    accept=".txt,.md,.markdown,.html,.htm,.docx,.json"
                    onChange={(e) =>
                      setSelectedFile(e.target.files?.[0] ?? null)
                    }
                  />
                  <label>Reconstruction profile for this import
                    <select value={importProfile} onChange={e => { setImportProfile(e.target.value); setImportModel(profiles?.profiles.find(p => p.id === e.target.value)?.model || ""); }}>
                      <option value="">Use reconstruction settings</option>
                      {profiles?.profiles.filter(p => p.enabled && p.id !== "fixture").map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                    </select>
                  </label>
                  {importProfile && <ModelPicker profile={profiles?.profiles.find(p => p.id === importProfile)} value={importModel} onChange={setImportModel} disabled={busy} label="Reconstruction model" />}
                  <button className="primary" disabled={busy || !selectedFile}>
                    Preserve & import
                  </button>
                  <details>
                    <summary>Processing budget</summary>
                    <label>
                      Input characters per call
                      <input
                        type="number"
                        min={12000}
                        max={120000}
                        value={importInputLimit}
                        onChange={(e) =>
                          setImportInputLimit(Number(e.target.value))
                        }
                      />
                    </label>
                    <label>
                      Output tokens per call
                      <input
                        type="number"
                        min={1000}
                        max={12000}
                        value={importOutputLimit}
                        onChange={(e) =>
                          setImportOutputLimit(Number(e.target.value))
                        }
                      />
                    </label>
                    <label>
                      Maximum model calls
                      <input
                        type="number"
                        min={1}
                        max={1000}
                        value={importCallLimit}
                        onChange={(e) =>
                          setImportCallLimit(Number(e.target.value))
                        }
                      />
                    </label>
                    <small>
                      Calls include analysis, reconciliation, final-scene
                      reconstruction, review, and repairs. Processing pauses at
                      the budget. Provider pricing is not estimated.
                    </small>
                  </details>
                  <small>
                    The selected reconstruction provider receives transcript
                    batches. Imported facts remain reviewable before becoming
                    canon.
                  </small>
                </form>
                <div className="panel">
                  <h2>Import history</h2>
                  {!jobs.length && (
                    <p>No sources imported into this timeline yet.</p>
                  )}
                  {jobs.map((j) => (
                    <div className="job-row" key={j.id}>
                      <FileText size={20} />
                      <div>
                        <strong>Import {j.id.slice(0, 8)}</strong>
                        <small>
                          {j.status} · {j.stage || "legacy"} · {j.processed}/
                          {j.total || "?"} passages
                        </small>
                        {j.error && (
                          <small className="text-error">{j.error}</small>
                        )}
                      </div>
                      <button
                        disabled={busy}
                        onClick={() => void inspect(j.id)}
                      >
                        Inspect
                      </button>
                      {["queued", "processing", "review"].includes(
                        j.status,
                      ) && (
                        <button
                          disabled={busy}
                          onClick={() =>
                            void run(async () => {
                              await post(`/imports/${j.id}/cancel`);
                              setJobs(
                                await api<Job[]>(
                                  `/branches/${branchId}/imports`,
                                ),
                              );
                            })
                          }
                        >
                          Cancel
                        </button>
                      )}
                      {j.method !== "agent-v2" &&
                        ["cancelled", "failed"].includes(j.status) && (
                          <button
                            disabled={busy}
                            onClick={() =>
                              void run(async () => {
                                await post(`/imports/${j.id}/retry`);
                                setJobs(
                                  await api<Job[]>(
                                    `/branches/${branchId}/imports`,
                                  ),
                                );
                              })
                            }
                          >
                            Resume
                          </button>
                        )}
                    </div>
                  ))}
                </div>
              </>
            ) : review.job.method === "agent-v2" ? (
              <>
                <button className="text-button" onClick={() => setReview(null)}>
                  <ArrowLeft size={16} /> All imports
                </button>
                <ImportReview
                  key={review.job.id}
                  id={review.job.id}
                  expectedCheckpointId={workspace.checkpoint.id}
                  onReanalyzed={inspect}
                  onApproved={async () => {
                    await loadWorkspace(workspace.branch.id);
                    setReview(null);
                    setPage("story");
                    setNotice("Reviewed reconstruction approved.");
                  }}
                />
              </>
            ) : (
              <>
                <button className="text-button" onClick={() => setReview(null)}>
                  <ArrowLeft size={16} /> All imports
                </button>
                <div className="panel">
                  <span className="badge">{review.job.status}</span>
                  <p>
                    This legacy import used paragraph review. Reanalyze its
                    unchanged source to consolidate claims and reconstruct the
                    ending.
                  </p>
                  <button
                    disabled={busy}
                    onClick={() =>
                      void run(async () => {
                        const job = await post<Job>(
                          `/imports/${review.job.id}/reanalyze`,
                          {
                            inputCharacterLimit: importInputLimit,
                            outputTokenLimit: importOutputLimit,
                            maxCalls: importCallLimit,
                          },
                        );
                        await inspect(job.id);
                      })
                    }
                  >
                    Reanalyze preserved source
                  </button>
                  <h2>{review.source.fileName}</h2>
                  <p className="hash">SHA-256 · {review.source.sha256}</p>
                  <p>
                    <strong>
                      {review.job.method === "ai-reconstruction-v1"
                        ? `AI reconstruction · ${review.job.provider} / ${review.job.model}`
                        : "Deterministic passage review"}
                    </strong>
                  </p>
                  {review.job.summary && <p>{review.job.summary}</p>}
                  <button
                    onClick={() =>
                      void run(async () =>
                        downloadSource(
                          review.source.id,
                          review.source.fileName,
                        ),
                      )
                    }
                  >
                    <Download size={15} /> Download unchanged original
                  </button>
                  <p>
                    Every candidate remains linked to its original evidence.
                    Accept only statements you want as canon, correct wording,
                    uncertainty, and knowledge scope before approval.
                  </p>
                </div>
                {review.candidates.slice(0, 20).map((c, index) => (
                  <div className="panel candidate" key={c.fact.id}>
                    <label className="check-label">
                      <input
                        type="checkbox"
                        disabled={review.job.status !== "review"}
                        checked={decisions[index]?.accept || false}
                        onChange={(e) =>
                          setDecisions((old) =>
                            old.map((d, i) =>
                              i === index
                                ? { ...d, accept: e.target.checked }
                                : d,
                            ),
                          )
                        }
                      />{" "}
                      Accept as canon · passage{" "}
                      {(c.evidence[0]?.ordinal ?? index) + 1}
                    </label>
                    <textarea
                      aria-label={`Fact ${index + 1}`}
                      disabled={review.job.status !== "review"}
                      value={decisions[index]?.text || ""}
                      onChange={(e) =>
                        setDecisions((old) =>
                          old.map((d, i) =>
                            i === index ? { ...d, text: e.target.value } : d,
                          ),
                        )
                      }
                    />
                    <label>
                      Claim type
                      <select
                        disabled={review.job.status !== "review"}
                        value={decisions[index]?.kind || "fact"}
                        onChange={(e) =>
                          setDecisions((old) =>
                            old.map((d, i) =>
                              i === index ? { ...d, kind: e.target.value } : d,
                            ),
                          )
                        }
                      >
                        <option value="fact">Fact</option>
                        <option value="rumor">Rumor</option>
                        <option value="belief">Belief</option>
                        <option value="secret">Secret</option>
                        <option value="correction">Correction</option>
                      </select>
                    </label>
                    <label>
                      Confidence ·{" "}
                      {Math.round((decisions[index]?.confidence ?? 1) * 100)}%
                      <input
                        type="range"
                        min="0"
                        max="1"
                        step="0.05"
                        disabled={review.job.status !== "review"}
                        value={decisions[index]?.confidence ?? 1}
                        onChange={(e) =>
                          setDecisions((old) =>
                            old.map((d, i) =>
                              i === index
                                ? { ...d, confidence: Number(e.target.value) }
                                : d,
                            ),
                          )
                        }
                      />
                    </label>
                    <label>
                      Who may know this?
                      <select
                        disabled={review.job.status !== "review"}
                        value={decisions[index]?.visibility || "narrator"}
                        onChange={(e) =>
                          setDecisions((old) =>
                            old.map((d, i) =>
                              i === index
                                ? { ...d, visibility: e.target.value }
                                : d,
                            ),
                          )
                        }
                      >
                        <option value="narrator">
                          Narrator only · private
                        </option>
                        <option value="public">Public knowledge</option>
                      </select>
                    </label>
                    <label>
                      Characters who know it (comma-separated)
                      <input
                        disabled={review.job.status !== "review"}
                        value={(decisions[index]?.knownBy || []).join(", ")}
                        onChange={(e) =>
                          setDecisions((old) =>
                            old.map((d, i) =>
                              i === index
                                ? {
                                    ...d,
                                    knownBy: e.target.value
                                      .split(",")
                                      .map((x) => x.trim())
                                      .filter(Boolean),
                                  }
                                : d,
                            ),
                          )
                        }
                      />
                    </label>
                    <details>
                      <summary>View original evidence</summary>
                      {c.evidence.map((s) => (
                        <blockquote key={s.id}>
                          <small>
                            Source passage {s.ordinal + 1} · Speaker:{" "}
                            {s.speaker}
                          </small>
                          <div className="prose">{s.text}</div>
                        </blockquote>
                      ))}
                    </details>
                  </div>
                ))}
                {review.job.status === "review" && (
                  <div className="panel">
                    <h2>Proposed story threads</h2>
                    {!threadDecisions.length && (
                      <p>
                        No unresolved promises, deadlines, or goals were
                        proposed.
                      </p>
                    )}
                    {threadDecisions.map((thread, index) => (
                      <label
                        className="check-label"
                        key={`${thread.title}-${index}`}
                      >
                        <input
                          type="checkbox"
                          checked={thread.accept}
                          onChange={(e) =>
                            setThreadDecisions((old) =>
                              old.map((item, i) =>
                                i === index
                                  ? { ...item, accept: e.target.checked }
                                  : item,
                              ),
                            )
                          }
                        />
                        <span>
                          <strong>
                            {thread.kind} · {thread.title}
                          </strong>
                          <small>{thread.details}</small>
                        </span>
                      </label>
                    ))}
                  </div>
                )}
                {review.job.status === "review" &&
                  review.job.proposedStateJson && (
                    <div className="panel">
                      <h2>Your resume checkpoint</h2>
                      <p>
                        Confirm the reconstructed scene, time, inventory,
                        skills, and participants. You remain the final
                        authority.
                      </p>
                      <label>
                        State (JSON)
                        <textarea
                          className="state-editor"
                          value={resumeState}
                          onChange={(e) => setResumeState(e.target.value)}
                        />
                      </label>
                      <button
                        className="primary"
                        disabled={busy}
                        onClick={() => void approve()}
                      >
                        <Check size={16} /> Approve checkpoint & reviewed facts
                      </button>
                    </div>
                  )}
              </>
            )}
          </section>
        )}
        {page === "providers" && (
          <section className="page narrow">
            <div className="eyebrow">CHOOSE HOW YOUR WORLD THINKS</div>
            <h1>Providers & settings</h1>
            <p>
              Credentials stay on your server. There are no silent provider
              fallbacks.
            </p>
            <form
              className="panel"
              onSubmit={(e) => {
                e.preventDefault();
                sessionStorage.setItem("occ-token", token);
                void run(async () => {
                  setProfiles(await api<Profiles>("/providers"));
                  await loadLibrary();
                  setNotice("Access token saved for this browser tab.");
                });
              }}
            >
              <h2>Workspace access</h2>
              <label>
                Application access token
                <input
                  type="password"
                  value={token}
                  onChange={(e) => setToken(e.target.value)}
                  autoComplete="off"
                  placeholder="Required only when APP_ACCESS_TOKEN is set"
                />
              </label>
              <button disabled={busy}>Save & reconnect</button>
            </form>
            {profiles && (
              <>
                <div className="panel">
                  <h2>Task routing</h2>
                  {Object.entries(profiles.tasks).map(([task, profile]) => (
                    <div key={task}>
                    <label>{task} connector
                      <select
                        value={profile}
                        onChange={(e) =>
                          setProfiles({
                            ...profiles,
                            tasks: {
                              ...profiles.tasks,
                              [task]: e.target.value,
                            },
                            taskModels: { ...profiles.taskModels, [task]: "" },
                          })
                        }
                      >
                        {profiles.profiles.map((p) => (
                          <option key={p.id} value={p.id}>
                            {p.name}
                          </option>
                        ))}
                      </select>
                    </label>
                    <ModelPicker
                      profile={profiles.profiles.find(p => p.id === profile)}
                      value={profiles.taskModels?.[task] || ""}
                      disabled={busy}
                      label={`${task} model`}
                      defaultLabel={`Use connector default${profiles.profiles.find(p => p.id === profile)?.model ? ` · ${profiles.profiles.find(p => p.id === profile)!.model}` : ""}`}
                      onChange={model => setProfiles({ ...profiles, taskModels: { ...profiles.taskModels, [task]: model } })}
                    />
                    </div>
                  ))}
                  <p>
                    Choose character and director profiles in each campaign's
                    Live world author settings. Separate character sessions use
                    the character profile, and the director coordinates
                    narration and memory. Task routing below also configures
                    narrator-only streaming and transcript reconstruction.
                  </p>
                  <button
                    disabled={busy}
                    onClick={() =>
                      void run(async () => {
                        await api("/provider-routing", {
                          method: "PUT",
                          body: JSON.stringify({ ...profiles.tasks,
                            narrationModel: profiles.taskModels?.narration || "",
                            reconstructionModel: profiles.taskModels?.reconstruction || "",
                            memoryModel: profiles.taskModels?.memory || "" }),
                        });
                        setNotice("Provider routing saved.");
                      })
                    }
                  >
                    Save task routing
                  </button>
                </div>
                {profiles.profiles.map((p) => (
                  <form
                    className="panel"
                    key={p.id}
                    onSubmit={(e) => {
                      e.preventDefault();
                      void run(async () => {
                        await api(`/providers/${p.id}`, {
                          method: "PUT",
                          body: JSON.stringify({
                            model: p.model,
                            enabled: p.enabled,
                            ...(["character", "director"].includes(p.id)
                              ? { adapter: p.adapter }
                              : {}),
                          }),
                        });
                        setProfiles(await api<Profiles>("/providers"));
                        setNotice(`${p.name} profile saved.`);
                      });
                    }}
                  >
                    <div className="section-heading">
                      <h2>{p.name}</h2>
                      <span className="badge">
                        {p.capabilities.available
                          ? "Configured"
                          : "Needs configuration"}
                      </span>
                    </div>
                    <p>{p.capabilities.note}</p>
                    <small>
                      {p.configured
                        ? "Server configuration present"
                        : "Server credentials not configured"}
                    </small>
                    {["character", "director"].includes(p.id) && (
                      <label>
                        Adapter
                        <select
                          value={p.adapter}
                          onChange={(e) =>
                            setProfiles({
                              ...profiles,
                              profiles: profiles.profiles.map((x) =>
                                x.id === p.id
                                  ? { ...x, adapter: e.target.value }
                                  : x,
                              ),
                            })
                          }
                        >
                          <option value="fixture">Deterministic fixture</option>
                          <option value="openai">OpenAI</option>
                          <option value="openai-compatible">
                            OpenAI-compatible
                          </option>
                          <option value="ollama">Ollama</option>
                          <option value="lemonade">Lemonade</option>
                        </select>
                      </label>
                    )}
                    <ModelPicker
                        profile={p}
                        label="Default model"
                        disabled={busy}
                        value={p.model}
                        onChange={(model) =>
                          setProfiles({
                            ...profiles,
                            profiles: profiles.profiles.map((x) =>
                              x.id === p.id
                                  ? { ...x, model }
                                : x,
                            ),
                          })
                        }
                      />
                    <label className="check-label">
                      <input
                        type="checkbox"
                        checked={p.enabled}
                        onChange={(e) =>
                          setProfiles({
                            ...profiles,
                            profiles: profiles.profiles.map((x) =>
                              x.id === p.id
                                ? { ...x, enabled: e.target.checked }
                                : x,
                            ),
                          })
                        }
                      />{" "}
                      Profile enabled
                    </label>
                    <div className="button-row">
                      <button disabled={busy}>Save profile</button>
                      <button
                        type="button"
                        disabled={busy || !p.enabled || !p.configured}
                        onClick={() =>
                          void run(async () => {
                            const result = await post<{ response: string }>(
                              `/providers/${p.id}/test`,
                            );
                            setNotice(`${p.name}: ${result.response}`);
                          })
                        }
                      >
                        Test connection
                      </button>
                    </div>
                  </form>
                ))}
              </>
            )}
          </section>
        )}
      </main>
      {deleteStory && <DeleteStoryModal id={deleteStory.id} name={deleteStory.name} onCancel={() => setDeleteStory(null)} onDeleted={() => {
        setCampaigns(rows => rows.filter(c => c.id !== deleteStory.id));
        if (workspace?.campaign.id === deleteStory.id) {
          generation.current?.abort(); setBranchId(null); setWorkspace(null); setReview(null); setJobs([]); setAction(""); setDraft(""); setAuthorView(false);
        }
        setDeleteStory(null); setNotice("Story deleted."); setPage("library");
      }} />}
      {newCampaign && (
        <div className="modal-backdrop">
          <form
            role="dialog"
            aria-modal="true"
            aria-labelledby="new-title"
            className="modal"
            onSubmit={(e) => {
              e.preventDefault();
              void create();
            }}
          >
            <button
              type="button"
              className="modal-close"
              aria-label="Close"
              onClick={() => setNewCampaign(false)}
            >
              <X size={20} />
            </button>
            <span className="eyebrow">MAKE ROOM FOR A NEW WORLD</span>
            <h2 id="new-title">Every story needs a beginning.</h2>
            <label>
              Campaign name
              <input
                autoFocus
                placeholder="Give your world a name"
                value={name}
                maxLength={100}
                onChange={(e) => setName(e.target.value)}
                required
              />
            </label>
            <label>
              Starting point
              <select
                value={synthetic ? "synthetic" : "empty"}
                onChange={(e) => setSynthetic(e.target.value === "synthetic")}
              >
                <option value="synthetic">
                  Crownspire Academy · synthetic example
                </option>
                <option value="empty">
                  Empty campaign · ready for an import
                </option>
              </select>
            </label>
            <p>
              {synthetic
                ? "An original academy setting with a starting scene, skills, inventory, and a first checkpoint."
                : "A blank campaign where you can import and review your own transcript."}
            </p>
            <button className="primary" disabled={busy || !name.trim()}>
              <Plus size={16} /> Create campaign
            </button>
          </form>
        </div>
      )}
    </div>
  );
}

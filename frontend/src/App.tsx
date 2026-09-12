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
import { api, downloadSource, post } from "./api";
import type {
  Branch,
  Campaign,
  Job,
  Profiles,
  Review,
  State,
  Workspace,
} from "./api";

type Page = "library" | "story" | "imports" | "branches" | "providers";
type Decision = {
  factId: string;
  accept: boolean;
  text: string;
  visibility: string;
};

export default function App() {
  const [page, setPage] = useState<Page>("library");
  const [campaigns, setCampaigns] = useState<Campaign[]>([]);
  const [workspace, setWorkspace] = useState<Workspace | null>(null);
  const [branchId, setBranchId] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [newCampaign, setNewCampaign] = useState(false);
  const [name, setName] = useState("");
  const [synthetic, setSynthetic] = useState(true);
  const [action, setAction] = useState("");
  const [token, setToken] = useState(sessionStorage.getItem("occ-token") || "");
  const [profiles, setProfiles] = useState<Profiles | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [review, setReview] = useState<Review | null>(null);
  const [decisions, setDecisions] = useState<Decision[]>([]);
  const [resumeState, setResumeState] = useState("");
  const [forkName, setForkName] = useState("An alternate path");
  const [forkCheckpoint, setForkCheckpoint] = useState("");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
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
      setPage("story");
    });
  const navigate = (next: Page) => {
    if (busy) return;
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
  const send = () =>
    run(async () => {
      if (!workspace) return;
      const controller = new AbortController();
      generation.current = controller;
      try {
        await post(
          `/branches/${workspace.branch.id}/turns`,
          { action, expectedCheckpointId: workspace.checkpoint.id },
          controller.signal,
        );
        setAction("");
      } catch (e) {
        if ((e as Error).name !== "AbortError") throw e;
        setNotice("Stopped. Reloading the last committed checkpoint.");
      } finally {
        generation.current = null;
        await loadWorkspace(workspace.branch.id);
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
        })),
      );
      setResumeState(JSON.stringify(workspace?.state, null, 2));
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
          <span className="version">FOUNDATION / 0.1</span>
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
                <button
                  disabled={busy}
                  className="campaign-card"
                  key={c.id}
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
                <strong>A foundation you can explore.</strong> Story turns are
                clearly labeled simulations. Imports use your review to
                establish canon; AI reconstruction is coming in a later
                milestone.
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
                <span className="badge">Fixture mode</span>
              </div>
              <div className="scene-location">
                <MapPin size={15} /> {workspace.state.location}
              </div>
              <div className="messages">
                {workspace.messages.map((m) => (
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
                <div ref={end} />
              </div>
              <form
                className="composer"
                onSubmit={(e) => {
                  e.preventDefault();
                  void send();
                }}
              >
                <textarea
                  aria-label="Your next action"
                  placeholder="What do you do next?"
                  value={action}
                  onChange={(e) => setAction(e.target.value)}
                  maxLength={4000}
                  disabled={busy}
                />
                <div>
                  <span>Free-form actions · Simulated continuation</span>
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
              <div className="state-section">
                <h3>REVIEWED FACTS</h3>
                {workspace.facts.length ? (
                  workspace.facts.map((f) => (
                    <div className="fact" key={f.id}>
                      <span className="badge">
                        {f.visibility === "narrator"
                          ? "Narrator only"
                          : "Public"}
                      </span>
                      <p>{f.text}</p>
                    </div>
                  ))
                ) : (
                  <small>No imported facts yet</small>
                )}
              </div>
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
                  <p>UTF-8 plain text or documented JSON · Up to 2 MiB</p>
                  <input
                    aria-label="Transcript file"
                    type="file"
                    accept=".txt,.json"
                    onChange={(e) =>
                      setSelectedFile(e.target.files?.[0] ?? null)
                    }
                  />
                  <button className="primary" disabled={busy || !selectedFile}>
                    Preserve & import
                  </button>
                  <small>
                    Word documents must first be exported as plain text. Sources
                    are never sent to AI in this milestone.
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
                          {j.status} · {j.processed}/{j.total || "?"} segments
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
                      {["cancelled", "failed"].includes(j.status) && (
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
            ) : (
              <>
                <button className="text-button" onClick={() => setReview(null)}>
                  <ArrowLeft size={16} /> All imports
                </button>
                <div className="panel">
                  <span className="badge">{review.job.status}</span>
                  <h2>{review.source.fileName}</h2>
                  <p className="hash">SHA-256 · {review.source.sha256}</p>
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
                    These candidates are verbatim source passages, not
                    AI-extracted facts. Accept only statements you want as
                    canon, edit their wording, and keep private information
                    scoped to the narrator. Unchecked passages will be rejected;
                    their source remains preserved.
                  </p>
                </div>
                {review.candidates.map((c, index) => (
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
                    <h2>Your resume checkpoint</h2>
                    <p>
                      Set the actual scene, time, inventory, and participants.
                      The existing state is shown as a starting point; the
                      parser does not infer these fields.
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
                    <div className="state-row" key={task}>
                      <span>{task}</span>
                      <strong>{profile}</strong>
                    </div>
                  ))}
                  <p>
                    Task routing is configured in the server environment. Memory
                    and AI reconstruction are not yet active.
                  </p>
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
                          }),
                        });
                        setNotice(`${p.name} profile saved.`);
                      });
                    }}
                  >
                    <div className="section-heading">
                      <h2>{p.name}</h2>
                      <span className="badge">
                        {p.capabilities.available
                          ? "Fixture available"
                          : "Adapter stub"}
                      </span>
                    </div>
                    <p>{p.capabilities.note}</p>
                    <small>
                      {p.configured
                        ? "Server configuration present"
                        : "Server credentials not configured"}
                    </small>
                    <label>
                      Model
                      <input
                        value={p.model}
                        maxLength={200}
                        onChange={(e) =>
                          setProfiles({
                            ...profiles,
                            profiles: profiles.profiles.map((x) =>
                              x.id === p.id
                                ? { ...x, model: e.target.value }
                                : x,
                            ),
                          })
                        }
                      />
                    </label>
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
                    <button disabled={busy}>Save profile</button>
                  </form>
                ))}
              </>
            )}
          </section>
        )}
      </main>
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

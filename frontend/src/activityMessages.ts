export const activityMessages = {
  preparing: [
    "Preparing the world for your next move.", "Gathering the threads of your story.",
    "Bringing the current scene into focus.", "Reading the moment before the next chapter.",
    "Connecting your action with the story so far.", "Gathering the context behind your next choice.",
    "Taking stock of the world you’ve shaped.", "Preparing the scene for what comes next.",
  ],
  "director-planning": [
    "The Director is coordinating the cast.", "The Director is setting the next scene in motion.",
    "The Director is choosing which voices should respond.", "The Director is arranging the next story beat.",
    "The Director is considering the shape of the scene.", "The Director is connecting your action to the cast.",
    "The Director is preparing the next round of reactions.", "The Director is deciding how the scene should unfold.",
  ],
  "cast-deciding": [
    "The cast is making decisions.", "The cast is weighing its next moves.",
    "The characters are considering their responses.", "The cast is finding its next words.",
    "The characters are weighing what matters to them.", "The cast is deciding how to meet this moment.",
    "The characters are shaping their next actions.", "The cast is responding through its own motives.",
  ],
  "director-assessing": [
    "The Director is weighing the proposed actions.", "The Director is assessing the stakes.",
    "The Director is considering what these attempts require.", "The Director is examining the uncertainty ahead.",
    "The Director is checking the limits of each attempt.", "The Director is weighing intention against circumstance.",
    "The Director is assessing how the world may respond.", "The Director is considering the challenges in this moment.",
  ],
  "director-resolving": [
    "The Director is resolving the next outcome.", "The Director is shaping the consequences.",
    "The Director is connecting actions with outcomes.", "The Director is determining what happens next.",
    "The Director is resolving the scene’s response.", "The Director is following each attempt to its outcome.",
    "The Director is working through the consequences.", "The Director is bringing the next story beat into focus.",
  ],
  narrating: [
    "The narrator is refining the world’s next scene.", "The narrator is weaving the next passage.",
    "The narrator is giving the moment its voice.", "The narrator is shaping the scene into words.",
    "The narrator is bringing the next chapter to life.", "The narrator is finding the rhythm of the scene.",
    "The narrator is turning this moment into story.", "The narrator is writing the world’s response.",
  ],
  "checking-continuity": [
    "Checking that the story remembers what came before.", "Reviewing the changes behind the scene.",
    "Connecting the new passage with established facts.", "Checking the scene against the story’s memory.",
    "Reviewing what changed and what stayed true.", "Checking that the world remains consistent.",
    "Examining the new scene for lasting changes.", "Keeping the next chapter connected to the last.",
  ],
  saving: [
    "Saving this moment in your story.", "Recording the next chapter in your timeline.",
    "Preserving the scene you’ve just shaped.", "Saving the story and its updated state.",
    "Adding this passage to your campaign’s history.", "Recording the world’s latest changes.",
    "Keeping this chapter ready for your return.", "Securing the next checkpoint in your story.",
  ],
  "import-preparing": [
    "Preparing your story for a closer reading.", "Turning your transcript into readable passages.",
    "Organizing the source before reconstruction.", "Preparing the pages behind your campaign.",
    "Bringing your transcript into a readable form.", "Arranging the source for the work ahead.",
    "Preparing the evidence behind your story.", "Making your transcript ready for reconstruction.",
  ],
  "import-queued": [
    "Your story is saved and waiting for its turn.", "Your transcript is ready in the reconstruction queue.",
    "The source is preserved; reconstruction will begin shortly.", "Your campaign’s past is waiting to be reconstructed.",
    "The transcript is waiting for the reconstruction worker.", "Your story is ready for the next available reconstruction slot.",
    "The source is safe while reconstruction waits to begin.", "Your import is queued for its first reading.",
  ],
  "import-extract": [
    "Reading the passages that shaped your world.", "Tracing characters and events through the transcript.",
    "Recovering the details scattered across your story.", "Following the story through its recorded passages.",
    "Looking for the people, places, and changes that matter.", "Gathering story details from the source.",
    "Reading the evidence behind your campaign.", "Recovering what the transcript establishes.",
  ],
  "import-reconcile": [
    "Connecting repeated details across the transcript.", "Comparing the story’s different accounts.",
    "Bringing scattered details into a coherent record.", "Checking how the story changed over time.",
    "Separating earlier details from later updates.", "Reconciling the threads recovered from the source.",
    "Comparing passages that describe the same story detail.", "Working out which details belong together.",
  ],
  "import-resume": [
    "Finding the scene where your story left off.", "Recovering the last recorded moment.",
    "Locating the point where the next chapter can begin.", "Reconstructing the scene awaiting your return.",
    "Checking who was present when the transcript ended.", "Recovering the player’s latest action.",
    "Bringing the final location and circumstances into focus.", "Preparing the scene from which your story can continue.",
  ],
  "import-audit": [
    "Checking the recovered details against their sources.", "Tracing each proposal back to the transcript.",
    "Reviewing the evidence behind the reconstructed world.", "Checking that the source supports the proposed details.",
    "Comparing the reconstruction with the recorded passages.", "Examining the support behind each story claim.",
    "Checking the final scene against its supporting evidence.", "Reviewing what the transcript confirms.",
  ],
  "import-repair": [
    "Revisiting details that need another pass.", "Working through the inconsistencies found in review.",
    "Correcting a response that needs closer attention.", "Checking uncertain details against the source again.",
    "Taking another pass at the details that did not validate.", "Refining the reconstruction where the evidence requires it.",
    "Reworking the parts that need correction.", "Addressing the issues found in the previous response.",
  ],
  resuming: [
    "Picking up from the last saved step.", "Resuming the work already preserved.",
    "Continuing from the saved reconstruction stage.", "Returning to the point where processing paused.",
    "Keeping completed work while continuing the next step.", "Resuming with the validated results already saved.",
    "Picking up the thread without starting over.", "Continuing the unfinished work from its saved state.",
  ],
} satisfies Record<string, string[]>;

export type ActivityGroup = keyof typeof activityMessages;
export const phaseLabels: Record<string, string> = {
  preparing: "Preparing context", "director-planning": "Director planning", "cast-deciding": "Character decisions",
  "director-assessing": "Director assessment", "director-resolving": "Resolving outcomes", narrating: "Narrating",
  "checking-continuity": "Checking continuity", saving: "Saving story", resuming: "Resuming saved work",
  "loading-saved": "Loading saved story",
  processing: "Processing story",
};
export function safeStoryPhase(phase: string, mode: string) {
  if (!(phase in phaseLabels)) return "processing";
  if (mode === "narrator" && ["director-planning", "cast-deciding", "director-assessing", "director-resolving", "resuming"].includes(phase)) return "processing";
  return phase;
}
export function activityPhrase(group: string, elapsedMs: number, seed = "") {
  const phrases = group === "story-resuming" ? activityMessages.resuming.filter((_, i) => i !== 2) : activityMessages[group as ActivityGroup];
  if (!phrases) return "Processing your request…";
  const offset = Array.from(seed).reduce((n, c) => (n + c.charCodeAt(0)) % phrases.length, 0);
  return phrases[(offset + Math.floor(Math.max(0, Number.isFinite(elapsedMs) ? elapsedMs : 0) / 8000)) % phrases.length];
}
export function elapsedText(milliseconds: number) {
  const seconds = Math.floor(Math.max(0, milliseconds) / 1000);
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

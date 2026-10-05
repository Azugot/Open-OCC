export default function ImportSteps({ current }: { current: number }) {
  return <ol className="import-steps" aria-label="Import progress">{["Source", "Reconstruct", "Review", "Continue"].map((name, index) =>
    <li key={name} aria-current={index === current ? "step" : undefined} className={index === current ? "active" : index < current ? "complete" : ""}><span>{index + 1}</span>{name}</li>)}</ol>;
}

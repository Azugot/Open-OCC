# Import and review

The first importer preserves source bytes before attempting any parsing. Original files are stored as PostgreSQL `bytea` alongside their filename, creation time, and SHA-256 checksum. They have no update/delete API. Database backups include all original files; there is no separate upload directory to lose.

## Accepted formats

- UTF-8 `.txt`, with blank lines separating passages. Speaker attribution is `unknown`; no heuristic claims to recover message boundaries.
- UTF-8 `.json`, with a root `messages` array. Each item must have string `role` and `content` fields. Array order is preserved. Extra fields are not interpreted, but remain intact in the original source.
- At most 2 MiB per file, 2,000 passages, 16,000 characters per passage, 100 characters per speaker, and 200 characters per filename. Archives, HTML, DOCX and images are not accepted in this milestone. Byte decoding is strict UTF-8.

```json
{
  "messages": [
    { "role": "user", "content": "I ask the clerk about registration." },
    { "role": "assistant", "content": "Registration closes at noon." }
  ]
}
```

Use `tests/fixtures/crownspire.json` for an original synthetic example. Export the supplied Word document to UTF-8 plain text if you want to try it; do not rename a DOCX extension to TXT.

## Job lifecycle

`queued → processing → review → approved`

Queued, processing and review jobs may be cancelled. Cancelled/failed jobs can be retried. The worker commits batches of up to 25 passages and their progress together. A restart resumes from the last completed batch. Unique `(job, ordinal)` constraints prevent duplicate segments. Cancellation races use an optimistic status check, rolling back a batch if cancellation won. The single worker sleeps between batches and performs no provider calls, so there is no token cost.

A malformed file becomes a failed job with its original bytes still available. Retrying identical malformed bytes will fail again: upload a corrected copy as a new source. This is deliberate preservation, not in-place repair. Parser errors terminate that attempt; database outages are retried by the worker.

## What review means

Each passage becomes a verbatim **candidate**, initially unchecked and narrator-only. This is deterministic segmentation, not semantic extraction. Reviewers must edit candidates into useful statements, accept/reject them, and explicitly set visibility. Original passages remain accessible through evidence links after edits. Rejected passages remain in the source but never enter approved canon.

Approve only material facts that are actually supported. Keep rumors and speculation out of public canon, and keep secrets narrator-only. Structured rumor/belief types and character-specific knowledge are later work. The fixture generation interface receives only accepted public facts; private facts are withheld completely.

The state editor starts with the existing checkpoint. Set the final scene, time, objective, condition, skills, inventory quantities and participants yourself. It accepts the documented JSON shape shown in the UI. Negative inventory, oversized fields, missing fields, or invalid JSON are rejected. Approval commits decisions and the new checkpoint together. Approval cannot overwrite a branch that advanced since upload: fork the upload's original checkpoint and import on that branch instead.

Imports do not replace the story view with the original transcript. That transcript remains in source review; the story view records approval and starts its continuation there. Searchable transcript history, automatic final-scene detection, agent reconstruction, correction reconciliation and portable campaign export are later milestones.

For the supplied Azugot transcript, future reconstruction should explicitly review initial-character corrections, source text truncation, incomplete HUDs, unclear message boundaries, and differing scene-header versus prose times/locations. It must preserve that campaign's own mechanics rather than substituting Crownspire's example.

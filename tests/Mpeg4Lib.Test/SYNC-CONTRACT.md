# Sync evidence and standalone output boundaries

A flat MP4 track without `stss` declares all samples independent. Its chunk reader
now exposes explicit `true` flags, so this is distinguishable from an unknown
fragment flag. USAC validation continues to override container flags from the
cleartext access unit's independence bit before writing or choosing preroll.

`Mp4aWriter.AddFrame(..., bool? sourceIsSync)` lists only confirmed `true` samples.
When some samples are dependent or unknown, it writes a sparse `stss`, including
an empty table when none is confirmed. Omitting that empty table would instead
claim that every sample was independent. The original three-argument overload
retains its historical all-independent output contract; encoded AAC-LC callers
can continue using it. Callers preserving source sync evidence use the nullable
four-argument overload. No compressed payload bytes are changed by this metadata
rule.

Lossless trimmed and split outputs require a confirmed entry point at or before
the requested start. Unknown frames do not discard an earlier confirmed preroll
run. Without a usable entry point, the operation rejects; split validation occurs
before invoking the callback that creates an output. Full remux preserves source
samples and accurately records their sync evidence, rather than inventing an
entry point. The generic multipart base's constructor and overridable sync/PCM
contracts remain source-compatible; lossless compressed routing opts into the
strict unknown-frame behavior.

A related filter-loop correction uses `TryComplete(error)` when notifying input
writers of failure. A channel may already have been closed by normal completion;
throwing again from `Complete(error)` previously replaced the actual filter/flush
failure with `ChannelClosedException`. The original failure now survives. Existing
`Mp4Operation` aggregate wrappers remain; the regressions inspect their exact
causal failure instead of changing that public operation API.

`SyncAdmissionTests` exercise writer true/false/unknown combinations and zero sync
entries, the legacy writer overload, actual USAC-header trim/split routing with no
preceding entry, callback ordering, retained unknown-frame preroll and exact flush
exception identity. `StssBoxTests` also checks positive absent-table evidence.
These are synthetic structural/routing fixtures, not actual USAC decoding or
player/listening acceptance. Existing presentation tests retain long USAC preroll,
nonzero edits and aligned/unaligned chapter boundaries. Codec consumer and
independent generated AAC-LC signal checks remain separate evidence.

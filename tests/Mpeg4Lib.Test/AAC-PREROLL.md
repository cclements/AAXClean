# AAC-LC standalone trim and split preroll

An AAC sync access unit still needs decoder overlap state for correct leading
PCM. Beginning an output exactly at a nominal sync frame can produce a valid
sample table and edit duration while losing its first audible samples. The
observed generated FDK AAC-LC input lost 448 leading samples in both an untrimmed
single remux and its first chapter when the source's priming edit was flattened.

The reader now dispatches one earlier AAC-LC access unit for flat tables, using
actual stts runs. Fragmented input without stts uses the time represented by
1024 core samples, conservatively covering 960-sample LC frames as well. Explicit
sync tables still constrain the entry point. Lossless sinks preserve a preceding
confirmed sync run even when the current AAC-LC frame is marked independent.
Multipart history retains two sync runs so an unaligned boundary inside the
previous frame still has its own preceding overlap state. The output edit hides
all retained preroll and preserves the requested presented interval. Media start
zero remains valid without a nonexistent previous packet. PCM and USAC entry
contracts are unchanged.

The distinction is corroborated by FFmpeg 8.1's MOV writer, which records an AAC
roll distance of one packet in `mov_preroll_write_stbl_atoms`:
https://ffmpeg.org/doxygen/8.1/movenc_8c_source.html#l03270
This is supporting implementation evidence, not ISO or broad profile admission.

Maintained `AacPrerollTests` cover 960/1024 sample trims and every split, original
media start, sample-duration-run transitions and a preceding SIDX segment. Final
tests against prior `dd5a89d` give 11 failures and one passing control; the corrected
full hermetic parser suite passes 157/157 in Release. Structural fixture bytes are
sample identifiers and are not themselves decodable AAC.

Separately, the isolated Libation presentation candidate's opt-in generated-audio
suite supplies six mono/stereo scenarios at 16/44.1 kHz, including fractional
endpoints and intro/outro removal. Independent FFmpeg/FFprobe checks verify all
nine single/chapter files: exact presented sample counts, contiguous unchanged
compressed source packets, and head/tail/whole-signal cosine correlation above
0.98 against the original synthetic PCM. The prior parser fails on a silent head;
source-overlay proof and final package proof are distinct retained stages.

No real HE-AAC, USAC, AC-4, provider, hardware/player or all-RID acceptance follows.
The fragmented fixture establishes dispatch and indexing only, not a new real
DASH decode claim. Re-encoding, sample-group metadata rewriting and general seek
admission remain separate contracts.

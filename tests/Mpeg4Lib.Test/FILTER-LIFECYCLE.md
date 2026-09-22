# Terminal filter completion contract

Frame filters have a single input producer. Serialize AddInputAsync calls and
finish input before CompleteAsync. CompleteAsync is repeatable and retains the
same terminal result; it does not admit concurrent input producers. Dispose is
not asynchronous completion: call and await CompleteAsync before disposing an
active chain. Empty filters retain the existing no-worker/no-flush behavior.

A closed input channel is a transport consequence of worker failure. Producers
and completion observe the worker task so the original processing exception is
preserved and cleanup does not run while that worker still uses resources.
Completion always closes input and joins every linked filter, even after an
upstream failure. The same exception observed through multiple stages is not
reported twice; independent failures are retained in an AggregateException.
Processing failures take precedence over cancellation during linked cleanup.

Maintained FrameFilterFailureTests and ChunkReaderFailureTests cover blocked
producers, partial buffers, failures during completion, repeated calls, successful
ordered drain, linked-worker joining, independent faults and cancellation.
Final tests against prior parser 8148435 fail 10 cases and pass four controls.
The corrected source passes all 169 hermetic parser tests in Release.

The companion Codecs pipeline-lifetime harness adds single/multipart upstream
failure after actual AAC output and while native encoder resources are live.
It verifies cleanup before Dispose with GC disabled, using the real native
counter/ASan probe. This is a separate native lane. It does not establish an
output publication journal, installed app, provider, other-RID or release proof.

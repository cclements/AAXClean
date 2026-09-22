# Failed filename construction owns cleanup

The filename constructors of Mpeg4File, Mp4File, AaxFile and DashFile open a
FileStream that the caller cannot dispose if construction throws. Their shared
initialization path now marks that input as internally owned and closes it on
both base parsing errors and derived format/admission failures. Loaded boxes are
cleaned best-effort without replacing the original exception. Cleanup is guarded
against repetition and avoids virtual disposal of a partially constructed subtype.

The public stream-taking constructors retain their previous failure contract:
if construction fails, the caller still owns the supplied stream. Successful
objects retain existing disposal behavior for either kind of input. Public
constructor signatures and additionalFixups defaults are unchanged. Derived
implementations can opt into owned failure cleanup through the protected
constructor and DisposeFailedConstruction helper.

Fourteen hermetic cases cover six filename failures before finalization, six
caller-stream failure controls and two successful ownership controls. Filename
cases run in a NoGCRegion and require immediate exclusive reopen; cleanup GC is
only performed after the observation. Prior source fails six with eight controls;
corrected full parser tests pass 189/189. Executed on macOS APFS; Windows/Linux
runtime proof remains separate. No provider file or secret is a fixture.

The separate app path that opens a parser from NetworkFileStream still owns that
input through its downloader. Exceptions after parser construction but before
assigning AaxFile deserve their own object/crypto cleanup check; they are not
covered by this filename-constructor contract. No finalizer-based cleanup claim
or broad caller-stream ownership change is introduced here.

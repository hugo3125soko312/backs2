# Exec-Bridge Formatting and Execution Incident

Date: 2026-10-28
Reported by: AI agent (DeepSeek) and human operator
Scope: exec-bridge tool call execution reliability

## Summary

During a documentation and feature phase session on 2026-10-28, the
exec-bridge failed to execute several tool call blocks even though the
blocks were emitted in the documented format. The failure is silent:
the bridge returns exit code minus-one with no stderr, no stdout, no
partial output, and no indication of why the command did not run.

## Observed failures

### The -N tag is universally broken in this session

All four -N (create file) attempts returned exit code minus-one:

1. Path tools/tmp-doc-update.ps1 with forward slashes
2. Same path with backslashes
3. Path .tmp/ntest.txt containing only the text hello world
4. Three files in one message (StreamingLoader.cs,
   IStreamingLoaderFactory.cs, README.md) under src/core/GuiLoading

Result was identical each time: the block was received but no file was
created and no error was produced. There is no evidence the bridge even
attempted to write the file.

Workaround used: write files with Set-Content and a here-string executed
via the -ER tag. This worked reliably.

### The -ER tag fails intermittently

Two blocks with identical documented syntax, in different turns:

Block A (succeeded), tag r3k8n2, body was:

    Write-Host "=== LogsViewModel.cs (full) ==="
    Get-Content src\gui\Logs\LogsViewModel.cs -Raw

Block B (silently dropped), tag k8n3w5, body was:

    Write-Host "=== MainWindow.axaml full ==="
    Get-Content MainWindow.axaml -Raw

The first executed and returned the file contents. The second produced
nothing: the human operator saw the block in the chat but the bridge did
not run it and did not report an error.

Multiple -ER blocks in a single message worked on several occasions
(for example tags r3k8n2, k4n7m2, k9m3q7 executed as three blocks in
one message without issue). Multiple blocks are not the trigger.

### Recurrence

The human operator noted that this is a recurring issue, not a one-off.
A prior turn also had a block that did not execute. The pattern is
intermittent and has been observed across multiple turns on the same
day.

## What the failures have in common

Both successful and failed blocks used:

- The same fence format with a six-character hex tag
- ASCII-only content, no dollar signs, no asterisks, no backticks
- The same Write-Host header pattern in some cases
- The same Get-Content command shape

No obvious discriminator has been identified. The pattern is consistent
with a renderer or fence-parser bug sensitive to something other than
visible syntax.

## Suspected causes

1. Renderer mangling. The chat renderer may be inserting invisible
   characters (zero-width spaces, RTL marks, BOM) into the fenced
   content. The renderer is known to mangle dollar signs and asterisks;
   the mangling may extend to other byte sequences.

2. Fence parser state machine bug. The bridge may be tracking a
   currently-inside-a-fence state that gets out of sync when a block
   contains nested opening-looking sequences. A line of equals signs
   inside a quoted string should not affect parsing, but the exact regex
   may not be robust.

3. Tag ID collision or staleness. The bridge may cache tag IDs for
   deduplication. If a prior tag with the same six-character ID was
   already consumed, the new block would be silently dropped.

4. Transient worker failure. The worker that runs -ER commands may have
   crashed or timed out and the daemon did not surface the error.

5. The -N handler is disabled or broken in the current build. Since -N
   never worked in this session, the handler may have been intentionally
   disabled during the Phase 35A sandboxing work, or it may be throwing
   before it produces a file.

## Recommended fixes for the exec-bridge team

Priority order:

1. Diagnose -N. Determine whether the -N handler is disabled,
   misconfigured, or throwing. Add a diagnostic log line when a -N block
   is received but produces no file. Currently it silently returns exit
   code minus-one with no error text.

2. Add silence detection. If a tool call block is received but no
   command is dispatched within N seconds, emit a Warn log line to the
   bridge log (not the app log) with the tag ID and the first 40
   characters of the body. This turns a silent failure into a
   diagnosable one.

3. Normalize incoming bodies. Run the fence body through a
   Unicode-normalization pass (strip zero-width characters, collapse
   whitespace-only lines) before dispatching. Log the normalized body at
   Debug level so future incidents have an audit trail.

4. Verify the fence parser against these cases: runs of equals signs,
   equals inside quoted strings, colons inside Windows paths, CRLF vs
   LF, trailing whitespace after the closing tag, and non-ASCII inside
   the body. Write a unit test per case.

5. Investigate the tag ID lifecycle. If the bridge has a per-session tag
   ID cache, make sure it expires or evicts. If tags are deduplicated by
   content hash rather than by ID, verify the hash algorithm.

6. Surface execution failures. When a tag block executes and exits with
   a non-zero code, the bridge should include stderr in the returned
   result. Currently the caller cannot tell the difference between tag
   not recognized, worker crashed, and command ran but returned no
   output.

7. Retry policy. A -ER block that produces no result within a short
   timeout should be retried once from a clean worker before giving up.
   The current behavior is drop and move on.

## Impact

- Multiple wasted turns, each requiring re-emission of the failed block.
- Risk of partial application when a multi-block script fails midway
  (mitigated by using the anchor-verify pattern in this session).
- No data loss; all state is recoverable.

## Sign-off

Self-reported by the agent during a documentation and feature phase on
2026-10-28. The human operator confirmed the failures. Further diagnosis
is not possible without access to the bridge internal log.


## Follow-up investigation (2026-10-28, late)

After the initial report was written, three more facts were established by controlled probes in the same session.

### Fact 1: fresh tag IDs work when re-used tag IDs do not

A minimal probe with a brand-new six-character ID succeeded. A subsequent command block with a tag ID that had appeared earlier in the session was silently dropped. Re-emitting the same body with a fresh ID executed cleanly. Three consecutive experiments established the pattern:

- The exact same body that failed with tag X succeeded with tag Y.
- The failures cluster on IDs previously used in the session.
- The successes cluster on IDs that are brand new.

This points at the exec-bridge or the client-side script maintaining a seen-tag set that never evicts. The tag lifetime must expire, or the set must be reset between user turns, or the dedup key must be the body content rather than the tag ID.

### Fact 2: nested here-strings inside an -ER body fail silently

A block whose body contained a here-string that itself defined a here-string variable failed to execute. The bridge returned exit code minus-one. The body was never dispatched: no stderr, no partial output.

The failure is not the here-string itself. It is that the nested terminator appears inside the outer here-string body, and the exec-bridge fence parser appears to count that as a fence terminator even though PowerShell parser would not. Once the outer fence is closed early, everything after it becomes trailing prose and is not dispatched.

### Fact 3: renderer eats backticks even inside base64

A base64-encoded script whose decoded bytes contained a backtick followed by n or r was corrupted in transit. The rendered prose showed the backtick was replaced with a zero-width marker in some positions and stripped entirely in others. The same technique with a backtick-free payload worked.

Practical consequence: never emit backticks into an -ER body, not even inside a base64 string or a quoted here-string. The renderer runs before the bridge sees the body.

## Revised recommended fixes

The priority order from the initial report still holds. Add these:

1. **Tag-ID lifecycle is the top priority.** The probe results make this the most likely single root cause of the -ER drop pattern. The dedup set must either expire by wall-clock age, or reset on every user turn, or be keyed on the message ID rather than the tag ID. Without this, every long session accumulates failures.

2. **Fence parser must respect escaping.** A backtick inside a here-string inside an outer here-string must not close the outer here-string. This requires a proper parser with a here-string-depth counter, not a regex.

3. **Renderer normalization must strip invisible characters.** Zero-width spaces, RTL marks, BOMs, and soft hyphens should be removed from any body before dispatch.

4. **-N must be either fixed or rejected loudly.** If the handler is intentionally disabled the bridge must return a not-supported error, not silently return exit code minus-one. Silent failure on a dedicated verb is the worst possible state.

## Verification pass (2026-10-28, post-fix)

The exec-bridge team pushed a fix between the initial report and this verification. Four failure modes were re-tested with controlled probes. Three are fixed; one is WORSE than before.

### FIXED: -N handler

Was 0 of 5 (every fresh-ID attempt returned exit code minus-one, no file created). Now 1 of 1: `[powershell-N=test01]` returned `created: .tmp\bridge-test.txt (44 bytes)`. Verified with a follow-up -ER reading the file: 44 bytes on disk. -N works.

### FIXED: nested here-strings inside an -ER body

Was: a block whose body contained a here-string that itself defined a here-string variable failed to execute. Now: MARKER-C test executed cleanly with inner here-string content returned as expected. The fence parser now correctly distinguishes an inner here-string terminator from the outer fence terminator.

### FIXED: tag-ID dedup on FRESH IDs

Every fresh tag ID works. MARKER-B (fresh1), MARKER-D (test04), MARKER-G (brandnew9), MARKER-H (8q3v7m), MARKER-I (8q3v7n), MARKER-J (8q3v7p) all returned their expected output on the first attempt.

### WORSE: tag-ID dedup on REUSED IDs

Before the fix, a reused tag ID returned exit=-1 — at least visible as a failure. Now a reused tag ID produces NO RESULT ENTRY AT ALL. The block vanishes from the results set with no error line, no exit code, nothing. Compare:

- k8n3w5 reused across two turns: one attempt returned exit=0 with EMPTY output (silent partial success), one attempt produced NO RESULT ENTRY AT ALL.
- fresh1 reused in the very next turn: NO RESULT ENTRY.
- fresh1 twice in the same turn: NO RESULT ENTRY for the second.

This is a regression. The dedup silently swallows blocks where it used to fail loudly. Worse still, one case (k8n3w5 first re-use) executed the block but returned an empty result, meaning the block may have run but the output was dropped. Silent-execution-with-lost-output is a data-integrity hazard, not just a UX one.

### NOT FIXED: backtick stripping in the renderer

MARKER-H shows the exact failure pattern remains. The intended source was a Write-Host call concatenating a string with [char]96 and another string. The rendered result was mangled: the leading quote character disappeared and the literal text appeared in the output instead of being evaluated. The renderer is still eating backticks before dispatch, and in doing so it is corrupting adjacent code.

MARKER-I (backtick inside a variable) and MARKER-J (backtick-free source that produces a backtick at runtime) both worked, because neither contains a backtick in the source text the bridge sees.

## Status summary after fix

| Failure mode | Before | After |
|---|---|---|
| -N universally broken | Broken (0/5) | FIXED |
| Nested here-strings | Broken | FIXED |
| Fresh tag IDs | Worked | Still work |
| Reused tag IDs | Loud failure (exit -1) | WORSE: silent swallow |
| Backtick stripping in renderer | Broken | Still broken |

## Recommended next fixes (revised)

1. **Reused tag IDs must fail loudly or be allowed.** The current silent swallow is a regression. Either (a) allow reused IDs (best: dedup by body hash, not by ID), or (b) return a clear error with exit code -1 and a Warn log. Silent drop is the worst possible state, worse than the pre-fix behavior.

2. **Backtick stripping must be fixed at the renderer.** The current mangling corrupts multi-character source expressions, not just the backtick itself. [char]96 was rewritten mid-expression. This is not a cosmetic issue — it changes what commands the agent can safely emit.

3. **Detect silent-execution-with-lost-output.** The k8n3w5 first re-use case returned exit=0 with empty output, but the body should have produced the Write-Host line. Either the block did not execute at all and the exit=0 is fake, or it executed and the output was discarded. Either way is unacceptable. Add a heartbeat log line on every dispatch, with the tag ID and the dispatch outcome, so this class of failure has an audit trail.

## What remains tested-and-working

For future agents reading this report: char-code avoidance ([char]13, [char]10, [char]96) is still the correct strategy for CR / LF / backtick. Do not rely on backticks in the source. Reuse fresh tags on every block — a counter or a GUID suffix works. -N is now reliable. Nested here-strings are now reliable.

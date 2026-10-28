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

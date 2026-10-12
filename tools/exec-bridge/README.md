# exec-bridge

Local chat-to-shell bridge for DeepSeek. A browser userscript detects
special fenced blocks in the chat, forwards them to a local daemon, and
returns results to the chat input.

## Components

| Component | File | Role |
|---|---|---|
| Userscript | `bds_exec_bridge.user.js` | Detects fenced blocks, injects Execute buttons, forwards Q&A, auto-sends results |
| Daemon | `exec_bridge.exe` | HTTP server on 127.0.0.1:7788. Executes commands, holds state |
| Q&A terminal | `exec_bridge.exe -qterm` | Interactive window for answering questions and approving filtered commands |
| Config | `exec_bridge.config.json` | Regex rules that require approval before execution |

## Fence tags

- `powershell-E=xxxxxx` - run the block, discard output
- `powershell-ER=xxxxxx` - run the block, return output to chat input
- `powershell-Q=xxxxxx` - ask the human a question (console-driven)
- `powershell-N=xxxxxx` - create a new file (not implemented)
- `powershell-C=xxxxxx` - edit an existing file (not implemented)

`xxxxxx` is a fresh 6-char lowercase alphanumeric id. Reusing an id means
the block is silently skipped as already-executed.

## Approval gate

Auto-fired commands matching a filter rule require approval at the qterm
window. Press Enter with an empty line to approve; type anything and press
Enter to deny. Manual clicks bypass the gate - you already clicked the
button, so asking again would be redundant.

Rules live in `exec_bridge.config.json`. Defaults cover git history
rewrite (amend, rebase, reset, force-push, filter-branch), tag and branch
deletion, and any write or delete touching `.git/`. Rules are regex;
the first match wins.

## Batch behavior

Every block in one assistant message is tracked together. Results wait
in a buffer until all blocks in that message complete. When the last
block resolves, all outputs and answers flush into the chat input as a
single message. Auto-fire on: the message auto-submits. Auto-fire off:
it waits in the input for you to press Send.

This prevents the model from racing ahead when a turn contains both an
`-ER` and a `-Q` block - the earlier bug being that the ER output would
land and auto-send while the Q answer was still pending.

## Running

`.\docs\pack.ps1` rebuilds `exec_bridge.exe` from source, kills any
running instance, and launches the daemon and qterm in two new windows.
Press Enter at the prompt.

Manual run:

    cd tools\exec-bridge
    go build -o exec_bridge.exe
    .\exec_bridge.exe
    # in a second terminal:
    .\exec_bridge.exe -qterm

## State

- `data/executed.json` - per-conversation dedup of block ids
- `data/` - git-ignored, all runtime state lives here

## Files

- `main.go` - entry point, flag parsing, server bootstrap
- `util.go` - shared helpers: ANSI constants, origin check, shell resolution, CORS
- `config.go` - filter rule loading and matching
- `storage.go` - exec dedup DB, question queue, approval queue, connection arbiter
- `http.go` - HTTP handlers and route registration
- `qterm.go` - interactive terminal client (questions and approvals)
- `exec_bridge.config.json` - approval filter rules
- `PROTOCOL.txt` - paste-into-chat protocol for the model
- `TOOLCALLS.txt` - human vocabulary mapping (read/run/ask/create/mod)
- `bds_exec_bridge.user.js` - the browser userscript

## Connection model

Single-tab holder. One browser tab holds the connection at a time, tracked
by the daemon. Other tabs can watch but auto-fire is disabled on them.
The holder sends a heartbeat every 10 seconds; a silent holder expires
after 30 seconds and the slot frees for another tab.
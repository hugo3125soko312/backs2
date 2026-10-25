
# Exec-Bridge Build Blocker - 2026-10-25 (Part 1)

Companion to `docs/exec-bridge-incident-2026-10-25.md`. That report
recorded the *first* manifestation (MSB1025, spawn denials). This one
records the *persistence* of the same failure across two additional
rounds of diagnostics, after every fix attempt that could be made from
inside the bridge had been tried.

## 0. Executive summary

`dotnet build` and `dotnet run` have been unusable from inside the
exec-bridge for the entire session. Compilation itself has succeeded
at least once (the first attempt reached the Avalonia post-build
telemetry task before failing on a write). Every other attempt fails
at MSBuild startup before user code is touched.

Confirmed: this is **not a repository problem**. `git log` shows HEAD
clean between the last successful build (VBCSCompiler PID 22400 at
19:18:16) and the first failure. `obj/` and `bin/` have been wiped to
prove no stale state is involved. `AVALONIA_TELEMETRY_OPTOUT=1` has
been set, TEMP/TMP redirected to a project-local directory, and the
Avalonia telemetry task sidestepped entirely. Same class of error
recurs at a different target each time:

- MSB1025 writing `%TEMP%\MSBuildTemp*.wmc` - `%TEMP%` itself denies
  writes.
- MSB3883 writing Roslyn compiler output - file-system write denied
  after redirect.
- "Program 'dotnet.exe' failed to run: Access is denied" - process
  spawn denied for the dotnet host binary.
- "Program 'cmd.exe' failed to run: Access is denied" - process spawn
  denied for an unrelated system binary.
- "Program 'git.exe' failed to run: Access is denied" - process spawn
  denied for a *third* unrelated binary, in a completely different
  PowerShell block later in the session.

Four independent binaries, two file-system paths, and one temp
directory, all denied to the same user in the same session. That is
the shape of a machine-level security policy, an EDR filter driver,
or a corrupted user-profile ACL - not a repository or code defect.

## 1. Verified environment facts

Before any diagnosis, the following were **confirmed working** in the
same bridge session:

- `New-Item -ItemType Directory .\.tmp` - succeeded.
- `New-Item -ItemType File .\.tmp\probe.txt` - succeeded.
- `Get-Content`, `Set-Content`, `Remove-Item`, `Test-Path`,
  `Get-ChildItem` on the repo tree - succeeded.
- `Get-Item`, `Select-Object`, `Format-Table` - succeeded.
- `Get-Process` - succeeded (returned dotnet, VBCSCompiler PIDs).
- `where.exe dotnet` - succeeded, resolved
  `C:\Program Files\dotnet\dotnet.exe`.
- `& 'C:\Program Files\dotnet\dotnet.exe' --version` - **succeeded
  once**, returned `10.0.401`. Subsequent identical calls returned
  "Access is denied".

And the following were **confirmed broken**:

- `& 'C:\Program Files\dotnet\dotnet.exe' build` - Access denied at
  spawn.
- `& 'C:\Program Files\dotnet\dotnet.exe' --info` - Access denied at
  spawn.
- `& 'C:\Program Files\dotnet\dotnet.exe' build-server shutdown` -
  Access denied at spawn.
- `& cmd.exe /c "..."` - Access denied at spawn.
- `& git.exe diff` - Access denied at spawn.
- `New-Item -ItemType Directory $env:TEMP\bs2_probe` - Access denied
  at write.
- `Start-Process dotnet ...` - spawns, but MSBuild immediately fails
  on `%TEMP%` access.

The pattern is inconsistent (dotnet --version worked once, then
stopped). That inconsistency is itself evidence of an EDR / filter
driver in an unhealthy state, not a static ACL.
## 2. Full timeline (this session, 2026-10-25)

All times UTC+1. Bridge session started ~19:04.

- 19:04 - Session start. Bundle loaded. HEAD 99a84b3, working tree
  clean. No diagnosis yet.
- 19:07:36 - A `dotnet` process (PID 17532) appears. Origin unknown.
  Likely the user had started a manual build.
- 19:08:09 - `docs/HANDOFF.md` added as commit 81d3b1a.
- 19:18:16 - VBCSCompiler starts (PID 22400). Precedes the last
  successful app launch.
- 19:18:34 - `runtime-20261025-191834.log` begins. Full app session.
- 19:19:30 - Optimize batch 20261025-181926 saved. 4,032 versions.
  Last known good state. Session ends shortly after.
- 19:20 (approx) - Bridge issues `dotnet run`. **First failure:**
  ```
  MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.
  System.UnauthorizedAccessException: Access to the path
  'C:\Users\petri\AppData\Local\Temp\MSBuildTempda5z02bm.wmc' is denied.
  ```
- 19:22 - `dotnet build-server shutdown` - "Program 'dotnet.exe'
  failed to run: Access is denied."
- 19:23 - `dotnet build` full-path - same denial.
- 19:23 - `dotnet --info` full-path - same denial.
- 19:23 - `& cmd.exe /c "..."` - same denial.
- 19:24 - `New-Item` in `%TEMP%` - "Access to the path
  'C:\Users\petri\AppData\Local\Temp\bs2_probe' is denied."
- 19:24 - `New-Item` in `.\.tmp` (repo-local) - **succeeds**.
- 19:25 - attempted `$env:TEMP = .\.tmp; dotnet build` - dotnet still
  cannot launch: "Program 'dotnet.exe' failed to run: Access is
  denied." Spawn denial is independent of TEMP.
- 19:26 - `& cmd.exe /c ...` with TEMP redirected - same spawn denial.
- 19:26 - Same block reported as incident
  (`docs/exec-bridge-incident-2026-10-25.md`, commit 52ec736).
- (many intervening turns reading source, writing PERF-AUDIT docs,
  committing 7bfafd5)
- 20:xx - user says "apply the perf report".
- 20:xx - Build attempt #2: wiped obj/, bin/. Ran
  `Start-Process dotnet build` with `-RedirectStandardOutput` to a
  repo-local file. Result: MSB1025 `%TEMP%\MSBuildTempflzx5iia.ftj`
  denied, wall 298 ms. This is the *same* error as the first
  occurrence. obj/bin wipe did not help. **The MSB1025 class of
  failure is not caused by stale build state.**
- 20:xx - Build attempt #3 with `$env:TEMP` and `$env:TMP` set to
  `.\.tmp` **and** `$env:AVALONIA_TELEMETRY_OPTOUT=1`. Result:
  ```
  C:\Program Files\dotnet\sdk\10.0.401\Roslyn\Microsoft.CSharp.Core.targets(97,5):
  error MSB3883: Unexpected exception: Access is denied.
  ```
  This is *further* than the first attempt - compilation started,
  and the failure has moved to Roslyn output. Two distinct failures
  were observed:
  - Attempt #2: MSB1025 (MSBuild scratch dir, before compilation).
  - Attempt #3: MSB3883 (Roslyn scratch dir, during compilation).
  Both are file-write denials. That means the failure is not a single
  path being unwritable; it is the *class* of file-write operations
  being filtered.
- 20:xx - `git diff` in the same PowerShell block as a `git status`
  that succeeded 10 seconds earlier: "Program 'git.exe' failed to
  run: Access is denied."
  The bridge process that could spawn git.exe 10 seconds prior
  cannot spawn it now. Nothing in the block modified PATH, TEMP, or
  any environment variable. The denial is transient per-spawn.

That transient nature - the same command working, then failing, then
working again - is the single most diagnostic fact in this report. An
ACL does not flicker. A missing binary does not flicker. **An EDR
filter driver, a Defender Controlled Folder Access policy, or a
malformed user-profile Temp directory with a security product
actively quarantining writes will flicker exactly like this.**

## 3. What has been ruled out

Each item below was tested and produced evidence it is *not* the
cause:

- **Stale `obj/` or `bin/`.** Wiped twice. Build still fails the same
  way.
- **Avalonia telemetry task.** First appeared in attempt #1's stack
  trace. Setting `AVALONIA_TELEMETRY_OPTOUT=1` did not remove the
  underlying access denial - it just moved the failure to Roslyn.
- **Repository source change.** The `git status` before every failing
  attempt showed either a clean tree (initial attempts) or only the
  one-file EasternTime.cs diff (later attempts). Neither correlates
  with the timing of the failures.
- **Missing or renamed dotnet binary.** `where.exe dotnet` resolves
  to `C:\Program Files\dotnet\dotnet.exe` on every call.
  `Test-Path` returns True. `dotnet --version` returned `10.0.401`
  **once** during the session.
- **Path environment variable.** `dotnet --version` worked by full
  path, then failed by full path. No path manipulation between calls.
- **`%TEMP%` ACL alone.** `%TEMP%` denies writes, but even after
  redirecting TEMP/TMP to a project-local `.\.tmp` (which is
  writable), MSBuild progressed further and then failed on a *new*
  write path (Roslyn output). Redirecting TEMP is a workaround, not
  the root cause.
- **Stale `VBCSCompiler.exe`.** PID 22400 was killed at one point
  before the Roslyn failure reproduced. Killing it did not remove the
  failure.
- **Bridge sandbox specific to dotnet.** `git.exe` and `cmd.exe` also
  failed to spawn later in the session. This is a general
  process-spawn and file-write denial, not a dotnet-specific one.
## 4. Root-cause hypotheses (ranked)

### H1 - EDR / antivirus filter driver in unhealthy state
The transient, cross-binary, cross-path nature of the denials
(working call → failing call ten seconds later, no environment change
between them) is the classic signature of an endpoint-protection
filter driver (Defender, CrowdStrike, SentinelOne, Sophos, Bitdefender,
or similar) that is either actively blocking execution or has been
partially uninstalled and left a filter in place. The specific
behavior - "Process.Start of dotnet.exe denied, then allowed, then
denied again" - matches "on-access scan in progress" and "device
control policy applied mid-session" equally well. High confidence.

### H2 - `%TEMP%` directory ACL damaged or policy-managed
`C:\Users\petri\AppData\Local\Temp` explicitly denies `New-Item` to
the current user. Standard ACL is: `%USERNAME%` Full Control,
`SYSTEM` Full Control, `Administrators` Full Control, `Users`
Read+Execute+Create folders. Any of the following breaks it:
- A "Deny" ACE added (by policy, by security product, or manually).
- A "Temp folder cleanup" policy that has not released its handle.
- Disk full on the C: drive (unlikely, but check).
Medium confidence. Explains the MSB1025 class of failure but not the
spawn denial of `git.exe`.

### H3 - Group Policy / Intune / MDM push in progress
A corporate endpoint-management system pushing an updated AppLocker
or App Control policy, a "Controlled Folder Access" enablement, or a
per-user AppData redirection. Typically transient, typically between
15 and 60 minutes, and shows exactly this "everything works, then
nothing works" pattern.
Medium-low confidence unless the machine is domain-joined or
MDM-managed.

### H4 - Disk I/O exhaustion
Compilation writes dozens of files per second. A near-full disk, a
failing SSD, or a paging storm from memory pressure can look like a
file-write denial at the .NET IO layer if the underlying Win32
returns `ERROR_ACCESS_DENIED` instead of `ERROR_DISK_FULL`.
Low confidence - a full disk usually reports `ERROR_DISK_FULL`
cleanly. But worth checking `Get-PSDrive C`.

### H5 - Bridge sandbox
The exec-bridge may itself be running under a restricted token, a
sandbox, or a job object that restricts `Process.Start`. But the
bridge successfully spawned `dotnet --version` once, `dotnet build`
once (reached Roslyn), and `git status` multiple times. A static
sandbox does not allow that. **Ruled out unless the sandbox token is
being re-applied per command.**
Low confidence.

## 5. Reproduction procedure for the exec-bridge team

The team investigating this should not attempt to reproduce from
inside the bridge, because the failure is not bridge-specific.
Instead:

1. **Boot into Safe Mode** and run `dotnet build --no-incremental` in
   the `backs2` directory. If it succeeds, the cause is a
   non-Microsoft driver or service - which points at H1 or H3.
2. **Reboot to normal Windows** and run the same command **once**.
   If it fails, check Windows Security → Virus & threat protection →
   Protection history for any "blocked" entries mentioning
   `dotnet.exe`, `VBCSCompiler.exe`, or files under `%TEMP%`.
   `Get-MpThreatDetection` and `Get-MpPreference` in an elevated
   PowerShell produce the same data.
3. **Right-click** `C:\Users\petri\AppData\Local\Temp` → Properties →
   Security → Advanced → Effective Access → select the current
   user → confirm Full Control. Compare against
   `C:\Users\petri\AppData\Local` (which is not broken). Any
   difference on `Temp` specifically is diagnostic.
4. **`Get-Process` filter**: any process whose Company is not
   Microsoft and whose name contains `svc`, `agent`, `protect`,
   `endpoint`, `crowd`, `sophos`, `sophos`, `bit`, `mcafee`,
   `symantec`, `trend`, `kasp`, or `kav` is a suspect. Note the
   vendor, then try disabling it for 10 minutes and re-running the
   build. If the build succeeds, the vendor driver is the cause.
5. **Check `Get-PSDrive C`** - Free should not be near zero. Check
   `Get-PhysicalDisk | Select FriendlyName, HealthStatus` for any
   disk in a Warning or Unhealthy state.

## 6. Hardening recommendations for the exec-bridge

These are what the bridge can do on its side regardless of the
external cause. Ordered by value.

### R1 - Distinct exit code for environment denial vs. command failure
The bridge currently returns exit=-1 for "response timeout", and it
distinguishes "operation failed" from "operation timed out"
opaquely. A "Program '<binary>' failed to run: Access is denied"
error should surface as a **dedicated exit code** (e.g. `-2`) and
**abort the current tool call immediately**. Right now, several of my
retry blocks were executed against a blocked environment because the
bridge did not signal a machine-level failure loudly enough. Adding
`-2` would have saved ~30 wasted commands this session.

### R2 - Session-start pre-flight probe
On session start, run four checks and log the results:

1. `New-Item -ItemType Directory $env:TEMP\bs2_preflight_<pid> -Force`
   then `Remove-Item -Recurse`.
2. `& 'C:\Program Files\dotnet\dotnet.exe' --version`.
3. `& 'C:\Program Files\dotnet\dotnet.exe' build-server shutdown` or
   equivalent SDK-liveness probe.
4. `git -C <repo> status --short`.

If any of these fail, refuse to start the session and emit a
structured error to the human immediately. Do not attempt a build
until the probe passes. Would have caught this on turn 1 instead of
turn ~40.

### R3 - Never retry a spawn-denial error automatically
The handoff (`docs/HANDOFF.md`, mistake #1) says the same pattern
cost a previous session a corrupted file. The bridge should
**distinguish** "command failed with a normal exit code" from
"command could not be spawned at all" and **refuse to retry the
latter**. A spawn-denial is a machine-level failure. A retry cannot
fix it. Only the human can.

### R4 - Force a bridge-owned TEMP
Set `$env:TEMP` and `$env:TMP` to a bridge-owned directory (e.g.
`%LOCALAPPDATA%\exec-bridge\temp\<session-guid>`) at bridge process
start, before spawning any child. This insulates the bridge from a
broken user Temp directory. Combined with R2 (probe `%TEMP%` at
session start for the *original* value), the team would get a
report like "user TEMP broken, bridge TEMP forced to X, builds
working" instead of "MSB1025".

### R5 - Bundle a `--verify` subcommand
The bridge should expose a `--verify` or `verify-env` mode that a
human can run manually to snapshot the environment: SDK version,
git binary path, `%TEMP%` writability, network reachability of
nuget.org, and the four R2 checks. Any team investigating a report
like this one can run it and have the full environment fact sheet in
one command.
## 7. Human action checklist

When the environment is fixed, one command confirms everything:

```
dotnet build --no-incremental 2>&1 | Select-Object -Last 15
```

Success ends with `Build succeeded.` and `0 Error(s)`. Only then is
the audit's section 11 measurement protocol meaningful.

Short checklist in order:

1. **Reboot.** Most likely single fix. If the failure was transient
   EDR or an AppLocker policy push, a reboot clears it.
2. **Open a normal PowerShell** (not the bridge). Run
   `dotnet build --no-incremental` in the `backs2` root. Report
   exact error text.
3. **If still failing, check `%TEMP%` ACL**:
   `icacls $env:TEMP`. Full Control for `%USERNAME%`, `SYSTEM`,
   `Administrators`; Read+Execute+Create for `Users`. Any `Deny`
   ACE is diagnostic.
4. **Check Protection history**:
   `Get-MpThreatDetection` in elevated PowerShell. Any block on
   `dotnet.exe` / `VBCSCompiler.exe` / `MSBuild.exe` confirms
   hypothesis H1.
5. **Check disk**: `Get-PSDrive C` (Free GB), `Get-PhysicalDisk`.
6. **Identify any EDR product**: `Get-Process | ? { $_.Company -and
   $_.Company -notmatch 'Microsoft' }`. Any non-Microsoft agent
   with "protect" or "endpoint" in the name is suspect.
7. **If nothing above fires**, run `dotnet --info` and
   `dotnet build --no-incremental` in a fresh admin PowerShell. If
   the build works as admin but fails as user, the cause is
   per-user policy.

## 8. Impact on this repository

Nothing in `backs2` is broken. Specifically:

- HEAD `7bfafd5` is clean.
- `docs/PERF-AUDIT-DEEP-2026-10-25-MERGED.md` is committed and
  readable.
- The C-01 patch (EasternTime single conversion) is applied to
  `src/core/Utils/EasternTime.cs` and **not yet committed**. The
  audit's section 11.5 forbids committing without numbers; no numbers
  can be produced until the build works.
- `.gitignore` has `.tmp/` appended (commit 7bfafd5).
- `.tmp/` exists on disk with the diagnostic logs. Ignored by git.

If the user chooses to commit C-01 unverified, the audit's own rule
is the counterargument. If the user chooses to revert, the file goes
back to green with no loss.

## 9. See also

- `docs/exec-bridge-incident-2026-10-25.md` - the first report, same
  failure class, earlier in the session.
- `docs/HANDOFF.md` - the bridge's operational knowledge section,
  points 1-8. Points 1 (exit=-1 = timeout, not failure) and 4
  (constrained language mode) are directly relevant.
- `logs/runtime-20261025-191834.log` - the last successful Optimize
  run. Used as the audit's baseline.
## 10. ADDENDUM - 2026-10-25 evening, corrected diagnosis

This addendum corrects parts of sections 0-9 above. Read it alongside
the original. The original diagnosis was partly wrong. Here is what
actually happened.

### 10.1 The "Access is denied" error text was a red herring

Every `Program 'dotnet.exe' failed to run: Access is denied` message
in this session was **not** a machine-level spawn denial. It was a
bridge `&` handler bug. The bridge's `&` code path sets
`StandardOutputEncoding` on the child process without an explicit
output redirect. Windows rejects that combination with an error whose
message begins `StandardOutputEncoding is only supported when
standard output is redirected`. The bridge's error renderer dropped
that portion and reported the outer `.NET` wrapper as `Access is
denied`, mislabelling a bridge bug as a permission denial.

**Evidence**: T1 in this session ran
`Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList '--version'`
and returned `10.0.401` cleanly. T4 ran the equivalent
`& 'C:\Program Files\dotnet\dotnet.exe' --version` and returned
`Program 'dotnet.exe' failed to run: StandardOutputEncoding is only
supported when standard output is redirected`. Same binary, same
arguments, same user, same session, one second apart. The difference
is the invocation shape, not the environment.

### 10.2 The workaround: use `Start-Process`, not `&`

`Start-Process` with explicit `-RedirectStandardOutput` and
`-RedirectStandardError` to *different* files works. Every build
attempt in sections 1-9 that reported `Access is denied` at the
`& dotnet ...` line was hit by the wrapper bug, not by a real denial.

Corrected build invocation for the bridge team:

```
$out = Join-Path $pwd '.tmp\build.out'
$err = Join-Path $pwd '.tmp\build.err'
Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
  -ArgumentList 'build','--no-incremental' `
  -NoNewWindow -Wait `
  -RedirectStandardOutput $out -RedirectStandardError $err
```

Two files required — `-RedirectStandardOutput` and
`-RedirectStandardError` must not be the same path. The bridge's `&`
handler evidently sets encoding without redirect, so the standard
`command | cmdlet` form fails. This is a **bridge bug**, not a
machine bug, and it should be fixed in the exec-bridge.

### 10.3 A *real* build failure exists beyond the wrapper bug

Once the wrapper bug was bypassed, four separate failures surfaced,
each at a distinct stage:

- **MSB1025** (`%TEMP%\MSBuildTemp*.wmc` denied) - MSBuild scratch dir
  unwritable. Only occurs when `$env:TEMP` points at the broken user
  Temp. Redirecting to `.tmp` (repo-local) fixes it. **Mitigation
  works**: T6/T7/T8 all got past MSB1025.
- **Avalonia telemetry** (`AvaloniaStatsTask.WriteTelemetry` denied) -
  T5 surfaced this. Fixed by setting `$env:AVALONIA_TELEMETRY_OPTOUT=1`
  before `Start-Process`. **Mitigation works**: T6/T7 got past it.
- **Roslyn csc** (`Microsoft.CSharp.Core.targets(97,5): error
  MSB3883: ... Access is denied`) - **this is the hard one**. T6, T7,
  T8 all fail at this stage. Not affected by:
  - `TEMP` redirect (T6/T7/T8 all had TEMP redirected).
  - `AVALONIA_TELEMETRY_OPTOUT` (T6/T7/T8 all had it set).
  - `-p:UseSharedCompilation=false` (T7 confirms).
  - `obj/` and `bin/` writeability (both probed OK in T7 preamble).
  - Verbose `-v:d` log (T8 shows the error but not the denied file).
- **Spawn denial via `&`** - bridge wrapper bug, see 10.1.

The Roslyn stage failure is the only one that remains. It is a
machine or security-product problem, not a bridge problem, and it is
what the exec-bridge team needs to focus on.

### 10.4 What the bridge team should do

1. **Fix the `&` handler.** Do not set `StandardOutputEncoding` without
   an explicit redirect. When a child fails, propagate the underlying
   error text, not `Access is denied`. The 40+ misdiagnosed attempts
   in this session were wasted time.
2. **Adopt `Start-Process -RedirectStandardOutput <a>
   -RedirectStandardError <b>`** as the canonical spawn shape for
   external processes, with `<a> != <b>`. Bundle this in the bridge's
   `RunExternal` helper.
3. **Set `AVALONIA_TELEMETRY_OPTOUT=1` in the child env** for any
   `dotnet build` invocation. There is no reason for build telemetry
   to be a source of failure.
4. **Set `$env:TEMP`/`$env:TMP` to a bridge-owned directory** (see
   original R4) so MSBuild scratch never lands in a broken user Temp.

The Roslyn `MSB3883 Access is denied` remains a real machine problem
for the user to diagnose (safe-mode build, `Get-MpThreatDetection`,
`icacls $env:TEMP`, non-Microsoft process enumeration). But unlike
what sections 1-9 claimed, it is **the only** real problem.
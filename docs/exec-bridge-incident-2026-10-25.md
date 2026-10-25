
# Exec-Bridge Incident Report - 2026-10-25

Reporter: DeepSeek via bridge. HEAD: 81d3b1a.
Host: Windows, dotnet SDK 10.0.401.

## Summary
At ~19:22 UTC+1 the exec-bridge sandbox lost the ability to
spawn child processes. dotnet.exe, cmd.exe, and dotnet --info
all return "Access is denied" at Process.Start. File I/O via
PowerShell cmdlets still works in the project tree, but %TEMP%
lost write permission even for New-Item.

## Sequence
1. dotnet run returns MSB1025: cannot create
   %TEMP%/MSBuildTemp*.wmc (access denied). First signal.
2. dotnet build-server shutdown returns "Program 'dotnet.exe'
   failed to run: Access is denied."
3. dotnet build (full path), cmd.exe /c return same.
4. New-Item $env:TEMP/bs2_probe returns "Access ... denied."
5. New-Item ./.tmp/probe.txt succeeds. Not universal.
6. where.exe dotnet resolves to C:/Program Files/dotnet/dotnet.exe;
   dotnet --version returned 10.0.401 once before block took.

## Failed mitigations
- TEMP/TMP redirect to ./.tmp: dotnet launch still denied.
- Full-path dotnet invocation: same denial.
- cmd.exe wrapper: same denial.
- build-server shutdown: cannot run.

## Hypotheses (order of likelihood)
1. EDR/Defender denying Process.Start on dotnet.exe and
   enforcing %TEMP% access mid-session.
2. ACL corruption on %TEMP% - explains MSB1025; may
   explain dotnet spawn denial if SDK init touches TEMP.
3. Leftover VBCSCompiler (PID 22400) holding dotnet host
   lock - unlikely; error is "access" not "conflict".
4. Defender Controlled Folder Access on SDK or %TEMP%.

## Recommended fixes for exec-bridge
1. Pre-flight probe at session start: New-Item inside
   %TEMP% and "& dotnet --version"; log both results on
   success and failure. Would have caught this in 1 turn.
2. Distinct exit code for process-spawn denial (e.g. -2),
   separate from -1 (timeout/failure). Agent would stop
   retrying and report environmental cause.
3. Log "where.exe dotnet" and "dotnet --version" in the
   session prelude.
4. If the bridge impersonates a token, force TEMP to a
   bridge-owned path so a corrupted host ACL cannot
   silently block MSBuild.

## Evidence
logs/runtime-20261025-191834.log (Optimize OK, 4032
versions, no GPU dispatch). MSB1025 stack trace from
dotnet run. "Program 'dotnet.exe' failed to run: Access
is denied." from dotnet build, --info, cmd.exe.
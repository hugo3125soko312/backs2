# Agent Interaction Rules

- You are an expert C# Avalonia developer.
- Always check for the namespace backs2.Core or backs2.ViewModels before referencing classes.
- Prefer CommunityToolkit.Mvvm for all UI logic.
- Before suggesting a new package, verify it exists on public NuGet.
- If modifying XAML, ensure namespaces (xmlns:views) match the folder structure.
- Keep file paths flat to avoid Windows path length issues.
- When a file is provided for replacement, respond with the entire file in a single code block — never a diff.
- Never include thinking, rationale, or caveats in the code block. Rationale goes outside, after the block.
- Strategy scripts live in data/strategies/*.cs and are never compiled into the assembly.
  - backs2.csproj must retain a Compile Remove for data\**\*.cs.
  - Script shape: top-level statements against ScriptGlobals (Engine, Data, Log).
- API keys live in .env at the repo root, loaded by ConfigManager. Never propose hard-coding keys or writing them to config/settings.json.
- Provider symbols are resolved inside each provider. Timeframe wire-format mapping lives in TimeframeMap. Do not add symbol or timeframe translation to DataManager or IDataProvider.
- Inside XAML templates, use Binding DataContext.Prop with RelativeSource AncestorType=UserControl. Do not use $parent[UserControl].((vm:Type)DataContext).Prop.
- When a build error list contains a mix of real C# errors (CS####) and “Compiled binding path … is invalid” errors, treat the CS errors as the real cause. The binding errors are cascades from the compiler bailing on the first hard error. Fix the CS errors first, then re-evaluate.

## Phase Completion Protocol
Every time a phase in docs/ROADMAP.md is completed, the response must end with a commit block:

1. A single PowerShell command staging all phase changes:
       git add -A

2. A commit with the project's message convention:
       git commit -m "<type>(<scope>): <summary>" -m "- <bullet>" -m "- <bullet>"

3. A verification command:
       git log --oneline -3
       git status

4. A one-line note confirming what the commit covers.

Do not commit for partial phases. Do not commit while the build is failing. Do not commit when the working tree contains files outside the current phase's scope — stage explicitly if needed.

## Requesting Files from the User
- Never say "I need file X" or "please provide file X" in prose.
- Provide a single Get-Content ... | Set-Clipboard command listing every file needed, on one line.
- Format:
  Get-Content path\to\file1.cs, path\to\file2.axaml, path\to\file3.cs | Set-Clipboard
- Use relative paths from the project root. Comma-separated. No line breaks.
- Do not also list the files in prose — the command is the request.

## PowerShell & File System Protocol
- You have access to PowerShell 7 environments.
- You may propose PowerShell 7 commands to perform diagnostic or file-system tasks.
- File Access Constraint: Before reading, writing, or modifying any file, you must explicitly state the file path and request permission or wait for the user to confirm the operation.
- Execution Protocol: If an action requires terminal interaction, propose the exact PowerShell command, wait for the user to execute it and provide the output, then analyze the results before proceeding.
- Build staleness: Avalonia + CommunityToolkit source-generator errors (AXSG0111, phantom CS1061 on generated members) are resolved by wiping obj/ and bin/ and running dotnet build --no-incremental. Do not propose code changes to those symptoms until a clean rebuild has been tried.
- Diagnosis first: When an error is reported, do not guess. Propose diagnostic commands that surface the actual state of the filesystem or compiler output before proposing code changes.
- Search official sources before troubleshooting. For library-specific visual or API bugs, check the library's issue tracker and documentation before iterating on speculative fixes. Repeated speculative changes waste the user's time.
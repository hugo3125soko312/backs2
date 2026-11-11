# Walkforward UI Wiring - AI Prompt & Completion

## Prompt (for AI model)

You are working on a C# Avalonia UI project at `C:/Users/petri/Desktop/programming/backs2`. The project has a `RunnerView.axaml` file that needs modifications to add walkforward settings UI.

## Task

Modify `src/gui/Runner/RunnerView.axaml` to:

1. Add `xmlns:views="using:backs2.Views"` namespace declaration after `xmlns:core="using:backs2.Core"`

2. Add `<UserControl.Resources>` block with `BoolToForegroundConverter` after `x:DataType="vm:RunnerViewModel">`:
```xml
    <UserControl.Resources>
        <views:BoolToForegroundConverter x:Key="BoolToForegroundConverter"/>
    </UserControl.Resources>
```

3. Wrap both MC Settings and WF Settings expanders in a StackPanel at Grid.Row="1":
```xml
    <StackPanel Grid.Row="1" Orientation="Vertical" Spacing="8" Margin="8,0,8,0">
        <!-- MC Settings expander -->
        <!-- WF Settings expander -->
    </StackPanel>
```

## Constraints

- Do NOT use Python for patching
- Do NOT create duplicate xmlns declarations
- Do NOT create duplicate UserControl.Resources blocks
- Make one change at a time and verify with `read` after each
- If you make mistakes, revert with `git checkout HEAD -- src/gui/Runner/RunnerView.axaml`

## Verification

- Run `dotnet build --no-incremental -c Release` to check for XAML errors
- DataGrid errors are pre-existing and expected
- Check structure with `grep -n "StackPanel\|Expander\|Grid.Row" src/gui/Runner/RunnerView.axaml`

## Completion (expected output)

The final structure should be:
- Grid.Row="0": Toolbar
- Grid.Row="1": StackPanel (MC Settings expander + WF Settings expander)
- Grid.Row="2": Subtabs grid (runner panel + results panels)

## Files to modify

- `src/gui/Runner/RunnerView.axaml`

---

# Fixing RunnerView.axaml Cleanly: Walkforward Settings Wiring

Target file: `src/gui/Runner/RunnerView.axaml`
Project: `C:/Users/petri/Desktop/programming/backs2`

## Goal

Three small edits, done one at a time, with a check after each:

1. Add the `views` namespace.
2. Add a `UserControl.Resources` block with `BoolToForegroundConverter`.
3. Put the MC Settings and WF Settings expanders together in one `StackPanel` at `Grid.Row="1"`.

The usual reason this goes wrong is duplicates (a second `xmlns:views`, a second `UserControl.Resources`) and a Grid whose row definitions and `Grid.Row` values no longer match. Fix those two things and the rest is easy.

---

## Step 0: Start from a known state

If the file is already half-patched, reset it instead of repairing it:

```bash
git checkout HEAD -- src/gui/Runner/RunnerView.axaml
```

Then look at what is there before touching anything:

```bash
grep -n "xmlns:\|UserControl.Resources\|Grid.RowDefinitions\|RowDefinition\|Grid.Row=\|Expander\|StackPanel" src/gui/Runner/RunnerView.axaml
```

Write down:
- which `xmlns:` lines exist
- how many `RowDefinition` entries the root Grid has
- which `Grid.Row` each top-level child currently uses

---

## Step 1: Add the namespace (one line)

Find the existing line `xmlns:core="using:backs2.Core"` and add this directly after it:

```xml
xmlns:views="using:backs2.Views"
```

Check that there is exactly one:

```bash
grep -c 'xmlns:views=' src/gui/Runner/RunnerView.axaml
```

The result must be `1`.

---

## Step 2: Add the converter resource

Place the block immediately after the opening `UserControl` tag, which ends with `x:DataType="vm:RunnerViewModel">`, and before any other content:

```xml
<UserControl.Resources>
    <views:BoolToForegroundConverter x:Key="BoolToForegroundConverter"/>
</UserControl.Resources>
```

Check that there is exactly one block:

```bash
grep -c 'UserControl.Resources>' src/gui/Runner/RunnerView.axaml
```

The result must be `2` (one opening tag, one closing tag). If the file already had a `UserControl.Resources` block, add the converter line inside it instead of creating a second one.

Also confirm that `BoolToForegroundConverter` really exists and is in the `backs2.Views` namespace:

```bash
grep -rn "class BoolToForegroundConverter" src/
```

If it lives in another namespace, change the `xmlns:views` value to match. A wrong namespace gives a "type not found" XAML error.

---

## Step 3: Wrap the expanders in a StackPanel

Before, the two expanders are separate children of the root Grid. After, they are children of one StackPanel:

```xml
<StackPanel Grid.Row="1" Orientation="Vertical" Spacing="8" Margin="8,0,8,0">

    <!-- MC Settings expander -->
    <Expander Header="MC Settings">
        ...existing content unchanged...
    </Expander>

    <!-- WF Settings expander -->
    <Expander Header="WF Settings">
        ...existing content unchanged...
    </Expander>

</StackPanel>
```

Rules for this edit:
- Remove any `Grid.Row="..."` attribute from the two expanders. They are no longer Grid children, so the attribute does nothing and only confuses the next reader.
- Keep the expander bodies exactly as they were. Move them, do not rewrite them.
- Make sure each `</Expander>` is inside the `</StackPanel>`.

---

## Step 4: Fix the rows, the part people miss

With the StackPanel at row 1, the root Grid needs three rows, and the subtabs content must move to row 2:

| Row | Content | Height |
|-----|---------|--------|
| 0 | Toolbar | `Auto` |
| 1 | StackPanel (MC + WF expanders) | `Auto` |
| 2 | Subtabs grid (runner panel + results panels) | `*` |

```xml
<Grid RowDefinitions="Auto,Auto,*">
```

(or the equivalent `Grid.RowDefinitions` block). Then:

```bash
grep -n "Grid.Row=" src/gui/Runner/RunnerView.axaml
```

Check that the toolbar is `0`, the StackPanel is `1`, the subtabs grid is `2`, and nothing else is claiming row 1 or 2 by accident. If the subtabs grid was previously on `Grid.Row="1"`, change it to `2`.

---

## Step 5: Verify

```bash
dotnet build --no-incremental -c Release
```

- DataGrid errors are pre-existing and expected. Ignore them.
- Any other XAML error is yours. Typical ones:
  - `type not found` for `BoolToForegroundConverter`: the wrong namespace, or the class is not public.
  - `duplicate attribute` or `duplicate xmlns`: Step 1 was applied twice.
  - `property Resources set more than once`: Step 2 was applied twice.
  - Unclosed or mismatched tags: the StackPanel or an Expander was closed in the wrong place.

Final structure check:

```bash
grep -n "StackPanel\|Expander\|Grid.Row" src/gui/Runner/RunnerView.axaml
```

Expected: a StackPanel at `Grid.Row="1"` containing both Expanders, and the subtabs grid at `Grid.Row="2"`.

---

## Working rules

- Edit with an editor or with the Edit tool. Do not patch with Python.
- Make one change at a time and re-read the file after each.
- If the file gets messy, revert it with the `git checkout` command from Step 0 and redo the steps in order. This is faster than untangling a bad patch.

---

## Final skeleton (for comparison)

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:backs2.ViewModels"
             xmlns:core="using:backs2.Core"
             xmlns:views="using:backs2.Views"
             x:DataType="vm:RunnerViewModel">

    <UserControl.Resources>
        <views:BoolToForegroundConverter x:Key="BoolToForegroundConverter"/>
    </UserControl.Resources>

    <Grid RowDefinitions="Auto,Auto,*">

        <!-- Row 0: Toolbar -->
        <Border Grid.Row="0"> ... </Border>

        <!-- Row 1: Settings -->
        <StackPanel Grid.Row="1" Orientation="Vertical" Spacing="8" Margin="8,0,8,0">
            <Expander Header="MC Settings"> ... </Expander>
            <Expander Header="WF Settings"> ... </Expander>
        </StackPanel>

        <!-- Row 2: Subtabs -->
        <Grid Grid.Row="2"> ... </Grid>

    </Grid>
</UserControl>
```

The `xmlns` lines and element names in this skeleton are illustrative. Keep the real ones from your file and only add what the steps above describe.

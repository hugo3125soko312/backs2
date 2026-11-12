# .NET SDK Resolution

## Problem

The build was failing with:
```
error MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found
```

## Root Cause

The project requires `.NET 8.0.428` SDK with desktop workloads, but the environment was using the scoop-installed .NET SDK (`C:\Users\petri\scoop\apps\dotnet-sdk\current`) which is version `11.0.100`.

### Workloads Issue

Even after installing `.NET 8.0` desktop workloads via `dotnet workload install`, the SDK resolver could not find them:
```
Workload ID '8.0' is not recognized
```

This indicated the workloads weren't properly installed or the SDK version didn't match.

## Resolution

### Step 1: Environment Variable Fix

The scoop-installed SDK was being used instead of the Microsoft installation. Fixed by:
```bash
set DOTNET_ROOT=C:\Program Files\dotnet
```

### Step 2: Workload Installation

Installed `.NET 8.0` desktop workloads. Verified with:
```bash
dotnet workload list
```
Output showed:
```
.NET SDK (from global.json): 8.0.428
auth               8.0.0.100 VS 18.12.12405.243
desktop             8.0.0.100 VS 18.12.12405.243
ios                 8.0.0.100 VS 18.12.12405.243
maccatalyst         8.0.0.100 VS 18.12.12405.243
macos               8.0.0.100 VS 18.12.12405.243
maui                8.0.0.100 VS 18.12.12405.243
maui-windows        8.0.0.100 VS 18.12.12405.243
windows             8.0.0.100 VS 18.12.12405.243
```

### Step 3: Build Verification

After workload installation, build succeeded:
```bash
dotnet build
# Build succeeded with warnings only (pre-existing XAML warnings)
```

## Key Details

- **Project TFM**: `net8.0`
- **Required SDK**: `8.0.428` with desktop workloads
- **Scoop SDK**: `11.0.100` (interfering)
- **Microsoft SDK**: `8.0.428` (correct)
- **Workloads**: Desktop workloads required for Avalonia applications

## Prevention

1. Set `DOTNET_ROOT` environment variable to point to Microsoft SDK
2. Ensure `global.json` specifies correct SDK version:
```json
{
  "sdk": {
    "version": "8.0.428",
    "rollForward": "disable"
  }
}
```
3. Use `dotnet workload list` to verify workloads are installed

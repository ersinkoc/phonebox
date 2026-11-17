# Antigravity diagnostics

`collect-antigravity.ps1` collects Windows installation, process, CLI, and
application-data **metadata** so a developer can identify a supported session
source without sharing private conversations.

The script does not read file contents, modify Antigravity files, transmit
data, or include credentials. Process command lines are redacted for common
token and credential argument names.

## Run on Windows

From PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\antigravity-diagnostics\collect-antigravity.ps1
```

To choose the report path:

```powershell
.\tools\antigravity-diagnostics\collect-antigravity.ps1 `
  -OutputPath "$env:USERPROFILE\Desktop\antigravity-diagnostics.txt"
```

Review the report before sharing it. It contains local paths, executable
metadata, process names, parent-process relationships, and candidate file
names, but not file contents.

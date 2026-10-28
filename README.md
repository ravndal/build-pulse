# DevOps Watcher

<img src="assets/app.png" width="96" alt="DevOps Watcher icon">

A small Windows tray app that watches selected Azure DevOps pipelines.

| Tray icon | Meaning |
|---|---|
| Green ✓ | Nothing running |
| Blue spinner | Building / deploying |
| **Flashing orange / red !** | A run is waiting for approval |
| Red ✕ | Can't reach the server |
| Small red dot | The latest finished run of a tracked pipeline failed |

- **Approvals** pop up in the bottom-right corner and stay until you **Approve**, **Reject** (asks you to confirm), **Open** it in the browser, or **Later** (snoozes it for 15 minutes; the icon keeps flashing).
- **Other status changes** (started, stage changed, succeeded, failed, ...) show as toasts that fade after about 6 seconds. You can mute them from the tray menu.
- **Left-click** the icon to open the dashboard: pending approvals plus the last 8 builds. Click a row to open it in the browser.
- **Right-click** the icon for Refresh, Settings, Mute, Start with Windows, and Exit.

## Install & updates

Download `DevOpsWatcher.exe` from the [latest release](https://github.com/ravndal/devops-pipline-watcher/releases/latest) and run it from a folder you can write to (for example `%LOCALAPPDATA%\DevOpsWatcher`).

The app checks GitHub for a new release 30 seconds after start and then every 6 hours. It downloads the new exe, checks its SHA-256 against the release, and restarts into it as soon as no approval is waiting. Right-click the tray icon → **Check for updates** to check now. Set `"AutoUpdate": false` in `settings.json` to be asked instead (a toast, then **Restart to update** in the tray menu).

### Releasing

CI builds every push to `main` and every pull request. To ship a version, push a tag:

```bash
git tag v1.2.0 && git push origin v1.2.0
```

The Release workflow publishes the single-file exe with that version and creates the GitHub release; installed apps pick it up on their next check.

## Build & run

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows/DevOpsWatcher.exe
```

To get a single self-contained exe:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Run with `--demo` to try the UI with fake pipelines and no server. A deployment asks for approval about 45 seconds after start.

## Setup

1. Settings opens on first run. Enter the organization URL (`https://dev.azure.com/yourorg`) or, for Azure DevOps Server, the collection URL (`https://tfs.company.com/tfs/DefaultCollection`).
2. Sign in with a **PAT** with these scopes: *Build (Read & execute)* and *Project and Team (Read)*. On-prem servers can use Windows authentication instead.
3. Click **Connect**, expand projects, and tick the pipelines you want to track.

Settings are saved in `%APPDATA%\DevOpsWatcher\settings.json`. The PAT is encrypted with Windows DPAPI for your user.

## Notes

- Approvals use the YAML pipeline approvals API (environment approvals and checks). Classic Release pipelines are not covered.
- `ManualValidation` steps (agentless jobs) are detected from the build timeline and pop up like approvals. By default you resume or reject them in the browser (Open → Review).
- **Experimental:** Settings has an option to Resume/Reject Manual validation steps from the popup. Microsoft documents no API for this, so the app posts the documented `TaskCompleted` plan event for the waiting task and then re-reads the timeline to confirm the step moved on. It's unverified against real runs, and it's unknown whether Azure DevOps enforces the step's approver list for this call. Test it first with `test-pipelines/manual-validation-test.yml` in a sandbox project.
- **Rate limiting** is set in Settings → Rate limits (stored in `settings.json` under `RateLimits`). `PerUser` is one budget for the signed-in user (server + token, or Windows account) across all calls; `PerEndpoint` adds budgets per endpoint: `projects`, `definitions`, `builds`, `timeline`, `approvals`, `approvalUpdate`, `planEvents`. A call that would exceed a budget waits for a free slot, up to `MaxWaitSeconds`, then fails with a rate-limit error. `Requests: 0` means unlimited; `Enabled: false` turns client limits off. A `Retry-After` from Azure DevOps always pauses that user's calls.

  ```json
  "RateLimits": {
    "Enabled": true,
    "PerUser": { "Requests": 120, "PerSeconds": 60 },
    "PerEndpoint": {
      "timeline": { "Requests": 60, "PerSeconds": 60 },
      "approvals": { "Requests": 10, "PerSeconds": 60 }
    },
    "MaxWaitSeconds": 30
  }
  ```
- Diagnostics are written to `%APPDATA%\DevOpsWatcher\log.txt` (no secrets).
- To approve, you must be one of the run's approvers. If you're not, the error shows in the popup.
- If the token can't read approvals, the app still detects waiting runs from the build timeline. You then approve from the browser.

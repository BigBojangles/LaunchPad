# LaunchPad

LaunchPad is a Windows desktop app for opening coding-agent projects in a persistent Linux VM. It supports Grok Build, Codex CLI, Claude Code and custom programs, with project/session selection and matching window indicators.

**Work in progress:** the source has migrated to Avalonia and Windows finishing is underway. Publishing this repository does not mark the current source, installer or isolation model as release-ready. The first Windows dogfood release comes before the Apple Silicon Mac port.

## Current work

Implemented features include project/session display names, a shared project menu, Settings, one-time tips, saved VM sessions and return/recovery handling. Display names never rename project folders. Physical UI acceptance and complete installed-build workflows remain outstanding.

The approved Windows work includes:

- Accurate working / needs-attention / idle indicators from agent events.
- Automatic Windows builds, tests and GUI testing through a separate non-admin working copy, with results returned to the Linux agent.
- Strict, Standard fenced and temporary Troubleshoot permission presets, plus installer setup repair.
- Optional user-configured email, Telegram, Discord or ntfy notifications. No LaunchPad account or messaging backend. Notifications start off; phone replies are later work.
- Finished-build testing, a bounded security scan/report and a verified Windows package.

These additions are planned, not all implemented. See [AGENTS.md](AGENTS.md) for the approved sequence and current status.

## Build the source

Use Windows and the .NET 8 SDK selected by `global.json` (currently 8.0.424). Avalonia dependencies are pinned and NuGet lock files are committed.

```powershell
dotnet restore LaunchPad.sln --locked-mode
dotnet build LaunchPad.sln --no-restore
dotnet test tests/LaunchPad.Tests/LaunchPad.Tests.csproj --no-restore --filter "Category!=Integration"
```

Source builds and UI tests do not require a published installer. VM execution additionally requires the Windows hypervisor, the non-admin launch account, the custom QEMU runtime and the compatible guest image chain. This repository does not contain those large runtime assets or user session disks.

The existing development layout uses a sibling `build-launch-qemu` directory. Installer/package scripts are under `installer/` and `scripts/`; a self-contained clean-install package is still being verified. Live integration and audit checks are opt-in and require their fixtures and prerequisites in the corresponding tests/scripts.

## Data and isolation

Project folders on the host and persistent VM session disks are separate. Keep backups of both. Recovery, upgrades and testing must preserve existing project work rather than reset a VM silently.

LaunchPad uses a non-admin Windows launch identity and guest restrictions. This is a developer tool under active testing, with known limitations:

- Loopback control endpoints currently lack authentication; loopback binding alone does not exclude other local processes.
- A controlled guest probe reached a service through the host's network-interface address. The proposed host-network enforcement has not been installed or verified.
- RAM/vCPU allocations exist, but a hard aggregate host resource cap is not established.
- Scoped process/file checks do not establish that every personal file is inaccessible or that all host isolation is complete.

The security presets and automatic Windows testing remain implementation work. No enterprise security or exhaustive audit claim is made. Raw audit reports, credentials, test outputs and machine-specific evidence are kept out of Git.

## Packages and license

Build outputs, VM images, local archives and `dist/` are intentionally ignored by Git. Publishing source does not publish or refresh installer assets; packaged releases will be verified separately. Historical local package documentation/checksums describe their own artifacts, not this source snapshot.

LaunchPad source is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses; QEMU is GPL-licensed. See [NOTICE](NOTICE). LaunchPad is not affiliated with or endorsed by xAI, OpenAI or Anthropic; their product names remain their respective trademarks.

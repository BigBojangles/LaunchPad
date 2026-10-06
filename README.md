# LaunchPad

**Put the agent in a box. Watch it work.**

LaunchPad is a Windows desktop app that opens a coding-agent project in a persistent Linux VM, with clear agent controls, each with its own color and icon. It is built for Grok Build, Codex CLI, Claude Code, and custom programs.

**Status:** public MIT source. Avalonia UI is in. The Windows side is still being finished. There is no public download yet. The next package will be a Windows beta release, not a fully tested stable release. An Apple Silicon Mac port comes after that Windows beta.

## What it does

1. Open a project.
2. Run it in a fenced Linux VM, or open native if you choose that path.
3. Work in agent terminals aimed at Grok Build, Codex CLI, Claude Code, or Custom.
4. Send selected files into the VM, then return changed files to Windows.

## Trust on the way back

- The return path scans files through Windows AMSI and refuses to apply if that scan is unavailable or rejects content.
- Copy-back requires a successful backup first and verifies the written host files.

Before returned VM files update the Windows project folder, LaunchPad can push a snapshot commit to a git remote you choose, for example a GitHub repo. That backs up the host project folder only, not the VM session disk. Your own day-to-day git commits stay separate.

## What it doesn't yet

- Activity lights that mean the agent is actually coding
- Notifications
- Automatic Windows testing bridge
- Security-preset slider
- Mac release
- User-facing product CLI
- Continuous or scheduled backup

## Known limits

- Native mode is unfenced.
- VM control endpoints currently lack authentication.
- Host-network isolation is incomplete.
- RAM/CPU allocations are not proof of an aggregate hard resource cap.
- File-access checks have been scoped, not exhaustive.
- Custom programs may lack activity telemetry.
- No integrated chat service, code editor, scheduled backup, or general application auto-update.
- No current Mac release.
- No promise of universal Windows build/GUI compatibility until the testing bridge is finished.

## Try it from source

Needs Windows and the .NET 8 SDK selected by `global.json`.

```powershell
dotnet restore LaunchPad.sln --locked-mode
dotnet build LaunchPad.sln --no-restore
dotnet test tests/LaunchPad.Tests/LaunchPad.Tests.csproj --no-restore --filter "Category!=Integration"
```

Source builds do not need a published installer. Running agents in a VM also needs the Windows hypervisor, the non-admin launch account, the custom QEMU runtime, and the guest image chain. Those large runtime assets and session disks are not in this repository.

When a beta package ships, install steps for that package will live here. Full VM installer and native-only packages are different. The README will match whichever one you download.

## Also available

Native is there if you want to run an agent on Windows without the VM. It is unfenced. The boxed path is the one this is built for.

## Credit

Built by Casey Nielsen · X @BigBojangles_ · github.com/BigBojangles

## License

LaunchPad source is under the MIT License. Third-party components keep their own licenses. QEMU is GPL-licensed. See NOTICE.

LaunchPad is not affiliated with or endorsed by xAI, OpenAI, or Anthropic. Their product names remain their trademarks.

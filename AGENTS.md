# Agent rules - LaunchPad

LaunchPad is a Windows-first coding-agent launcher. Windows finishing is active; Apple Silicon Mac work starts after the first Windows dogfood release.

## Layout and preservation

| Path | Purpose |
|---|---|
| `src/LaunchPad/` | Application source. Avalonia views use `.axaml` and `.axaml.cs`. |
| `tests/LaunchPad.Tests/` | Automated tests; private generated evidence belongs under ignored `TestResults/`. |
| `installer/`, `scripts/` | Windows packaging, runtime preparation and opt-in verification tools. |
| `dist/` | Live local distribution package. Refresh when publishing packages; never delete as junk. Ignored by Git. |
| `v1.0.* release/` | Historical local package archives, intentionally retaining their original branding. |
| Sibling `../build-launch-qemu/` | Custom QEMU, guest templates and persistent session disks. Not source-repository payload. |
| `trash/` | Authorized local quarantine of confirmed duplicates, preserving relative paths. Never package or commit. |

1. Preserve project folders, credentials, persistent VM disks, recovery data and required image backing chains. Never silently reset a project to recover.
2. Do not rename `BuildLaunchTest` or the sibling `build-launch-qemu` without an explicit request. User-facing branding is LaunchPad.
3. Edit in place; do not scatter extra full app copies, new top-level folders or note-file piles. Plan/status/lessons belong here. Generated private evidence stays in existing build/test output locations.
4. Preserve Fence/QEMU/launch protocol behavior unless the approved task is that defect. Do not substitute the custom console-size QEMU build without verification.
5. After installer/path changes, verify the installed LaunchPad directory contains the executable, QEMU, images and sessions before claiming installed fencing works.
6. Casey owns product priorities, UI acceptance and risk exceptions. Implement approved work autonomously; ask only for consequential missing decisions or mandatory access. Progress updates require no reply.
7. Implement logical chunks and prove them at phase end. Avoid builds after every small edit, repeated broad matrices and unrelated evidence expansion.
8. Real user projects and their original session disks are not automated test fixtures. Use owned disposable fixtures; preserve failed diagnostics and recovery data.
9. Never send external messages or notifications from development tools without explicit authorization. Product notification setup does not authorize test messages to real recipients during implementation.
10. Commit/push and creation of a public MIT GitHub repository named LaunchPad are explicitly authorized for the current source-publication checkpoint. Do not interpret this as authorization for release tags, installer uploads or publishing a finished release.
11. Keep secrets, local agent configuration, VM assets, session state, raw reports and quarantined files out of Git. MIT covers LaunchPad source, not third-party QEMU/agents or guest software.

## Approved execution plan (2026-10-06)

This section supersedes earlier audit-first/enterprise-hardening release gates. The current sequence is Windows features -> polish and Casey's testing -> bounded release scan/report -> Windows dogfood package -> physical Apple Silicon Mac port -> ongoing fixes.

### A. Documentation and current interface

- Larger changes require a parallel read-only documentation review at kickoff. The implementing agent remains the single writer. Record decisions and Mac lessons as work progresses; reconcile status and proof at phase end. Reviewers do not duplicate tests/scans.
- Inline project/session display names: Enter saves, Esc cancels, empty input uses folder name, and Reset to folder name restores it. Persist in existing records, never rename folders or change identity. Use the same name on tiles, titles and matching marks; ellipsis plus full-name hover for long text.
- Folder menus keep four or five primary actions and put others under More. Hover ellipsis opens the same menu as right-click.
- Top-bar Settings is app-wide; per-project actions stay on the folder menu. Persist Settings and one-time tips, with a tips switch/reset. No permanent info icons or pointer-following hints.
- Finish focus, scrolling, loading/errors, keyboard access and scaling; Casey accepts physical UI and workflow polish.

### B. Truthful status and shared events

- Separate machine lifecycle from agent activity. Green means observed active work, yellow a confirmed question/choice/approval awaiting attention, red idle/stopped, gray starting/disconnected/unknown. Failures have explicit text.
- Keep project identity borders separate from labeled activity lamps. Home and edge indicators consume the same state.
- Agent adapters use supported events without changing prompts, policies, approvals or instructions. Include session/run identity, event identity and timestamps for reconnection/deduplication.
- Do not infer completion from silence/CPU/process existence. Unsupported agents/custom programs show truthful lifecycle and unavailable activity.
- Shared events support notifications and relevant policy alerts; policy blocks have their own reason/badge, separate from activity.

### C. Automatic Windows testing

- The coding agent remains in Linux. Automatically stage a project snapshot into a managed Windows working copy, run builds/tests/GUI apps as non-admin BuildLaunchTest, and return exit codes/logs/selected artifacts through a request/result interface.
- No manual file/log transfer, guest account credentials, admin shell or native Windows coding-agent mode.
- GUI testing uses a separate interactive test desktop with a clear return path. Validate this early; the existing noninteractive private desktop is insufficient.
- Cancel only the owned test process tree, preserve failures/results for recovery, and never silently apply test changes to the real project. Host apply is an explicit conflict-checked project action.

### D. Permission presets and setup repair

- Three positions: Strict, Standard fenced (default), Troubleshoot. Standard supports ordinary coding/controlled testing; Strict adds approved-destination restrictions and confirmation before Windows tests.
- Troubleshoot adds named project-scoped exceptions, expiring after 30 minutes or session end. Never admin, blanket host-file access or an unfenced slider position. Preserve the existing separate unfenced action.
- Display actual blocks/reasons and effective policy; never advertise unenforced restrictions. Required privileged provisioning belongs in installer setup, with Repair setup afterward. Repair preserves project/settings/credential/session state; runtime remains non-admin.
- Preserve configurable RAM/vCPU allocations. Do not call those a proven hard aggregate host-resource cap.
- Known control-port authentication, host-network reachability and incomplete host-file coverage concerns remain explicit design/release findings. Deferred host-network research is not an installed/verified protection or ordinary-launch gate.

### E. Optional notifications

- Global default off; user-owned provider setup in Settings. After setup, expose per-project on/off on the folder menu, never the VM tab strip. No minimum-duration slider in v1.
- One run-finished notification per run and one needs-attention notification per distinct actual question. Deduplicate reconnect/restart events. Run finished does not mean project complete/tests passed.
- Email first, plus Telegram, Discord webhook and ntfy adapters. No LaunchPad messaging account/backend/per-message charge; provider limits apply. Credentials remain host-side outside the guest.
- Send project/agent/run identification and a brief outcome, never transcripts/files/secrets. Independent host monitoring/delivery continues with running sessions after the main window closes; sleep/offline delivery limits are explicit.
- Report failures locally; avoid blind resends after ambiguous acceptance. Outbound alerts only; phone replies remain later work.

### F. Finish, verify and release Windows

- Finish startup/import/agent readiness sequencing, persistence, recovery, concurrency and window association. Optimize demonstrated bottlenecks without weakening durable writes.
- Casey tests the complete build and requests polish/features before Windows acceptance.
- Then use the existing scan runner once against the completed candidate; record tool/candidate identity, real coverage, practical findings and limits. No exhaustive CVE cleanup, repeated general scans or enterprise-hardening campaign.
- General package findings are backlog unless they establish material product exposure. Material unresolved data-loss/isolation/install/workflow defects must be disclosed for Casey's release decision; never silently turn a failure into a pass.
- Package the selected runtime manifest and complete image chain, required QEMU components, licenses/NOTICE and per-asset checksums. Verify clean install, repair and upgrade without developer sibling folders; preserve records/settings/credentials/session disks and review uninstall behavior.
- Do not flatten images or aggressively trim runtime payload merely to chase size. Make measured changes with backing/state compatibility proof.
- Prepare the local dogfood release; release tags/assets/external publication require separate authorization.

### G. Mac and later work

- After Windows release, reuse Avalonia and shared project/session/event logic. Use native Apple Silicon adapters, ARM64 Linux guest, host credential storage and equivalent product behavior.
- Physical Mac verification is required. Cross-compilation and Windows ACL/account results do not establish Mac parity.
- Carry Windows lessons forward, then dogfood/targeted fixes and separately authorized PRs. Deeper hardening and remote notification replies remain later work.

## Current status and evidence

- Avalonia migration, editable names, common menus, Settings/tips, state/recovery handling and scoped non-admin Windows launch controls are implemented. Physical UI, real sign-in/history and complete installed-build acceptance remain unfinished.
- Latest recorded current-source checks: 188 automated checks passed plus one ordinary disposable VM launch/version probe passed under the non-admin launch identity, with normal guest shutdown. These are bounded checks, not a release/security certification.
- Status components exist, but reliable activity telemetry is unfinished. Windows testing bridge, security presets, notifications, complete installer repair/package and Mac implementation are pending.
- Runtime assets/dist have not been refreshed by source publication. Existing hashes/version docs refer to their particular artifacts.
- Known limits are documented in README: unauthenticated loopback channels, observed host-interface service reachability, no proven aggregate hard resource cap and scoped rather than exhaustive host-file isolation.
- Private machine/evidence chronology was preserved locally under ignored test outputs before preparing public contributor guidance. Raw reports and original user-state identifiers are not public repository material.
- End each phase with what changed, focused proof, limitations, remaining defects and next action. Reuse valid evidence; repeat affected checks only after meaningful changes/failures or a changed candidate.
- No defensible completion date yet. Refine the estimate after validating interactive Windows GUI testing. Do not mark unfinished/hardware-unavailable objectives complete.

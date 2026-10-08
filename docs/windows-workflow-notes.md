# Windows implementation notes — 2026-10-08

Internal implementation map for Casey's next design pass. README is owned separately.
This is not release acceptance or permission to install/publish. Source stays in src;
the current review executable is dist/LaunchPad.exe. Existing Native artifacts already
exist; they do not need a new product implementation from scratch.

## Tonight's scope and receipts

- Combined UI 1–14 implemented except supplied stock provider artwork. First/final
  UI batch passed 10 checks. Gear moved to bottom-left; caption drag area spans the
  full client width above content, with window buttons and resize grips above it.
- Exact 16-color palette unchanged. ProjectIdentity.IndexFor supplies both ring and
  terminal-color indexing, independent of display sorting/renaming.
- Windows Terminal: per-tab color. Supported Windows 11 classic console: border/title.
  A shared Windows Terminal outer frame is not repainted as one project's identity.
- Recovery warnings now appear after history collection/status disposal/VM shutdown
  and heartbeat stop. This fixes a concrete modal dependency; it does not prove the
  reported physical close freeze is entirely resolved.
- Affected host batch passed 66 checks after one enum-name compile correction.
- Grok v2 hook state projection is implemented and socket-tested, not live accepted.
  New capture preserves nonce-scoped prompt hashes and explicit child-session state.
  Old v1 data remains diagnostic-only. No hook state can queue a completion page,
  approve a command, open a host resource or trigger copy-back/shutdown.
- Do not infer completion from Stop: another Stop hook can continue the turn.
  Matched idle_prompt/root SessionEnd supplies red. Session-level notices may have
  no prompt ID; current-prompt IDs are checked whenever supplied. Parallel tool
  completion preserves yellow. A new prompt or actual idle/end clears it; precise
  same-turn question-resolution reporting remains a provider capability gap.
- Reconnect needs fresh root binding; stored hook state is not live proof.
  A connected hook lamp means **last reported state**, not measured CPU activity or
  continuous proof of coding. Missing callbacks can leave that last state stale.

Official Grok hook contract and Stop-gate caveat:
https://docs.x.ai/build/features/hooks
https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/docs/user-guide/10-hooks.md

## Close/save: the unresolved ownership path

FenceSession.WatchTui owns normal agent-exit collection and safe return application.
SessionGuardian independently owns a dead terminal's recovery and guest shutdown.
On terminal X, the latter currently preserves a recovery copy and leaves Windows
unchanged. It is not the requested automatic safe copy-back.

Do not simply auto-apply from both owners. The next repair needs one generation/PID-
bound return owner, a bounded handoff on terminal X, and fallback to preserved
recovery if the desktop is absent. The desktop can apply only the complete verified
receipt with scanner, host baseline and local-original checks. Guardian's restricted
account must not gain general access to Casey's Windows folders. Main-window exit
with live terminals must retain their independent owner. Reuse the current return
proof; one normal /exit and one X observation are still needed tomorrow.

Source: Services/Fence/FenceSession.cs, SessionGuardian.cs, SessionCompletion.cs,
ReturnApplier.cs, LiveSession.cs and Views/FenceDialog.axaml.

## Credential map — inventory/design only

| Scope | Current location/owner | Current behavior | Next choice to design |
|---|---|---|---|
| Pager secret | LaunchPad host NotificationDestinationStore + WindowsNotificationSecretProtector | Current-user platform protection; opt-in; Disconnect removes owned local secrets | Keep Off separate from Disconnect; provider revocation separate |
| Grok host reuse | GuestAuth under LaunchPad/grok-home; auth.dpapi and retained legacy auth.json | New caches protected with current-user DPAPI; new-user reuse off; old explicit choice retained | Explicit legacy migration/removal; sharing separate from per-VM login |
| Grok guest login | /home/builder/.grok/auth.json | Persisted inside writable session disk; trusted parent checkpoints host reuse when enabled | Session-only / remember in this project / scoped Forget |
| Codex guest state | /home/builder/.codex | Allowed/persistent guest state; CLI vs serving-process versions can differ | Verify exact auth inventory without reading secret values before implementing Forget |
| Claude guest state | /home/builder/.claude and .claude.json-related paths | Allowed/persistent state; exact auth files and installed version not verified here | Same scoped inventory; never erase the entire state/history directory |
| Guest history bundle | GuestHome bundle.tar under per-project grok-home storage | Current guest export includes Grok sessions, not a universal all-agent auth backup; host bundle is not claimed DPAPI-encrypted | Keep history and login retention independently controllable |
| Native login | Provider's own Windows credential/state storage | LaunchPad uses existing native permissions/sign-in | Explain provider ownership; no silent host logout or deletion |

No credential values were read for this map. A reuse switch does not disable saved
per-VM sign-ins. Session-only and Forget need exact provider/version storage contracts
and tests that preserve project files/history/tools. OAuth revocation is provider-side.

## Settings/options map — preserve until tomorrow's design pass

| Surface | Existing options/source | Scope |
|---|---|---|
| SettingsWindow | Theme, tips/reset, default agent, RAM/CPU defaults | App-wide; Theme defaults Match Windows |
| SettingsWindow | Grok reuse, setup Repair | App-wide; reuse is not all-agent credential retention |
| NotificationSetupWindow / SettingsWindow | Channel setup, self-email/presets, enable, Disconnect, test page, history | Optional app-wide pager; no sends authorized by this map |
| ExistingProjectsView menu | Open, folder, saved VM work, send files, agent; More recovery/memory/permissions/Windows tests/rename/reset/native | Project actions stay here; pager switch appears only after global setup |
| NewProjectDialog | Parent folder and optional saved default | Creation workflow; onboarding/picker acceptance deferred |
| ProjectPermissionsWindow | Standard, Strict, temporary exceptions | Effective enforcement matters; refuse unsupported Strict |
| SessionBoard/SessionMark | Display names, board-only grouping, identity rings and state lamps | Session presentation; no filesystem rename/move |
| EdgeBarWindow | Running-window switching, Pin | Window navigation; not a settings tab or pager toggle |

Source persistence: Models/AppSettings.cs, Services/SettingsStore.cs and existing
KnownProject records. No whole-app scale option was added by tile fitting; it only
changes board tile/icon size. Full menu/Settings restructuring remains deferred.

## Inner sandbox compatibility (“double box”)

Keep the outer VM plus enforced AppArmor, UID1000, NoNewPrivs and zero effective
capabilities. An inner agent sandbox adds a boundary; it does not replace the outer
policy or guarantee every integration/file tool has the same scope.

| Provider | Supported candidate configuration | Known dependency / limitation |
|---|---|---|
| Grok | --sandbox workspace (Linux Landlock) | Off by default upstream; workspace still permits .grok/temp and in-process network. Verify the exact bundled version supports the flag and denied writes |
| Codex | --sandbox workspace-write; exclude /tmp and inherited TMPDIR from broad command write roots | Namespace creation/bubblewrap compatibility needs an actual command check. Current documentation describes Landlock compatibility; do not force deprecated flags or infer fallback from CLI starting |
| Claude | sandbox.enabled=true; allowUnsandboxedCommands=false; failIfUnavailable=true | Linux bubblewrap+socat; pin/check version semantics. Bash sandbox does not cover built-in file tools/MCP/hooks. A missing dependency must not silently disable the promised inner sandbox |

Retained historical pins: Grok 1.0.46, outer Codex 0.160.0, serving process reported
0.160.1; Debian kernel 6.1.0-53-amd64. Claude version is unverified. These are evidence
pins, not a new live-version inventory. The reported unprivileged_userns_clone=0
explains the specific failed bubblewrap command, not all possible Codex executions.
The retained AppArmor profile also lacks mount permissions needed by nested bwrap;
namespace sysctl changes alone are not a compatibility proof.

Do not blindly set a VM-wide sysctl, grant broad mounts/capabilities, change to an
unconfined profile or use disable-sandbox as a repair. The small next proof is an
owned stopped candidate with one inside-project write and one denied sibling write
through the effective provider command sandbox, plus normal launch/tool/log/history
compatibility. Root-managed launch configuration must win over project attempts to
disable it; preserve project instructions and unrelated personal configuration.

The source entry now supports an additive /etc/launchpad/inner-sandbox.json manifest
with root-owned ancestry and exact selected-binary hash pins. Unsupported/unpinned
manifest entries fail closed. The example intentionally has null hashes/version
inputs: it must not be installed as a working policy. Manifest absence preserves
legacy launch; those images are explicitly not verified as double-boxed. Errors
other than ENOENT are not treated as absence. This pins the entry binary, not every
provider-serving/updater/plugin dependency; the bundle compatibility manifest still
needs their identities and actual command-boundary proof. Codex temp exclusions
also need a compatible project-scoped temporary-directory/export policy before
normal builds can be claimed working.

Current preparation is source-only. Activation of installed/sibling templates and
saved sessions is outside the authorized workspace boundary and needs Casey's named
approval. No existing guest has been changed or claimed double-box verified.

Primary references:
https://docs.x.ai/build/features/sandbox
https://learn.chatgpt.com/docs/sandboxing
https://learn.chatgpt.com/docs/permissions
https://learn.chatgpt.com/docs/config-file/config-reference
https://code.claude.com/docs/en/sandboxing

## Workspace portability

The retained root exporter walks /home/builder/in/project only and omits .git,
symlinks/nonregular files and unchanged baseline content. Policy also permits named
provider state/tool/cache homes, /tmp and /var/tmp. Therefore nonproject files can
remain VM-only; source review does not show Casey actually lost project code.

scripts/guest-portability.py prepares a bounded before/after metadata report for
allowed nonproject write roots, with project/generation-bound private baselines and
descriptor-relative no-follow traversal. Complete successful baselines are removed
after their aggregate report is durably saved; incomplete/skipped cases retain them.
It is not yet invoked by the shipped guest parent or surfaced by the host UI.
Never auto-export HOME or credentials. Mark truncation/skipped links/unreadable
roots explicitly. Metadata-only changes cannot detect same-size/same-time tampering
and do not prove confinement; this is a recovery aid, not a security scanner.
Classify provider state, caches/tool installs and temporary locations separately.
Show a warning/review path if unexpected persistent files changed; safe recovery
copies must stage into a separate reviewed project recovery payload, never silently
overwrite Windows files. A future narrowed policy needs normal tool compatibility.
Do not claim filename extensions alone identify every source file or secret.

## CLI edition plan — reuse the existing runtime

Existing distribution: LaunchPad-Setup.exe + three required .bin parts for Full;
LaunchPad-Native-Setup.exe and LaunchPad-Native-win-x64.zip already exist. Native uses
the same host executable with native-only.txt, not another VM implementation. These
older packages are not rebuilt by the current dist/LaunchPad.exe source handoff.

CLI is a third frontend to the same fenced runtime, not raw qemu arguments:

| Command | First supported behavior |
|---|---|
| launchpad new --path PATH --agent AGENT | Create/register a chosen folder without overwriting an existing project; no forced path |
| launchpad list [--json] | List known projects and actually owned sessions; no machine startup |
| launchpad run --project PATH [--agent AGENT] | Selected agent, persistent overlay, exclusive disk owner, interactive terminal; explicit agent choice wins |
| launchpad save --session ID | Request a verified return through the existing live owner; preserve conflicts/recovery |
| launchpad stop --session ID | Graceful close/save with bounded recovery; never blanket process kill |

RAM/CPU options use the same validation/defaults. Custom programs need an explicit
path and existing transfer constraints. No separate service account/login system.
The VM CLI package still requires QEMU, immutable image/maintenance and licenses;
Native remains the small package without these. Publish different artifact names
and checksums; never let a marker from Native contaminate Full/CLI selection.

Implementation sequence:

1. Add a small console frontend to a shared runtime API; --help/list must not start
   Avalonia or provision an account/VM. Program currently has internal headless
   helper modes, not a supported public VM command interface.
2. Extract terminal handoff from FenceHost/TuiWindow.Show: CLI attaches stdin/stdout
   directly to the existing channel; GUI still opens its normal session window.
3. Replace ProjectReturnHost UI callbacks with console reporting/conflict results.
   Keep one durable session owner and receipts independent of frontend lifetime.
4. Expose save/stop using a generation/PID-bound live-owner request, not new competing
   receivers. Existing static in-process LiveSession is insufficient across CLI calls.
5. One focused owned CLI create/run/save/reopen proof and package-path check at the
   phase end. Use current runtime/maintenance APIs; no duplicate VM/session store.

Suggested exits: 0 success, 2 invalid input, 3 unavailable runtime/setup, 4 already
open, 5 preserved conflict/recovery, 130 user interruption. JSON output contains
typed identity/state/errors, not tokens or terminal transcripts. A later noninteractive
agent automation interface is separate from running the ordinary interactive TUI.

This is a real shared-runtime extraction, not a trivial zip or an already working
CLI release. Implementation and release packaging remain deferred as Casey agreed.

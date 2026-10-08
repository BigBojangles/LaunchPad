# Direct-boot experiment - completed basic workflow, 2026-10-08

Branch: try-direct-boot. Main checkpoint: 732e8d9. No merge or push.
Main source/distribution, installed runtime and poc-openvmm remain unchanged.

## Current result

| Requested step | Result | Evidence and limit |
|---|---|---|
| Worktree-local app/runtime bundle | PASS | dist contains EXE, copied QEMU, complete three-image backing chain, matching kernel/initrd and maintenance payload. Actual package planner validated all eight runtime inputs. App PublicRuntime.Ensure resolved this bundle and supplied direct boot for a new session. |
| Grok/custom mismatch | PASS, test setup corrected | Guest parser reads EOF while fence is disconnected and defaults to Grok on an empty import. Old runner connected after IMPORT-READY. Corrected runner connects fence first, as the app does. Guest reported AGENT-IN custom and AGENT-PICK custom. No production agent-selection code or guest policy was changed. |
| Import/typing/shutdown/reopen | PASS for basic backend workflow | Text contents and 16,384-byte binary SHA256 verified from inside guest; terminal input wrote/fsynced a unique token; actual app MachineShutdown helper completed two clean ACPI shutdowns; same overlay reopened with the token intact. Both launches selected direct boot. |
| Branch commit | PASS | Corrected proof, pinned bundle manifest and current report committed on try-direct-boot. Payload/evidence remain ignored, not committed to Git. |

The successful test used one disposable session through its first boot and reopen.
A previous failed proof session remains retained for diagnosis. No real project,
existing saved disk, source template or image backing chain was changed.

## Runtime bundle

Run dist/LaunchPad.exe manually. The qemu executable beside it lets normal app
runtime discovery select this bundle without an environment override. The proof
runner explicitly selected the same local root because its process is dotnet.
The installed application is not switched to this runtime.

scripts/direct-boot-runtime.json records the exact experimental runtime manifest.
The staged copies under dist/images are:

- debian-12-builder-runtime-20261006-policy2.qcow2
- debian-12-builder.qcow2
- debian-12-nocloud-amd64-20260601-2496.qcow2
- launchpad-maintenance-runtime-20261006-policy2.kernel
- launchpad-maintenance-runtime-20261006-policy2.initrd
- launchpad-maintenance-runtime-20261006-policy2.tar.gz
- runtime.json and maintenance.json

Every copied runtime artifact matched its declared SHA256. Kernel release:
6.1.0-53-amd64. Kernel SHA256:
d66b8bc4b8330f4e98257602449feeeed696b860bf147a40477e7f4cfc48e704.
Initrd SHA256:
cd032cc68333d4b79a3196ce67d7164f402305b123e1b0d6b32c412a13b2fdbe.
Existing immutable kernel/initrd files serve both boot and maintenance; no duplicate
pair is packaged. QEMU/DLLs/firmware/licenses are local copies. No rebase or flatten
was needed. Bundle construction used only source reads and worktree writes.

Normal boot retains q35/WHPX, resources, writethrough caching, networking and all
four integration channels. It adds -kernel/-initrd and:
root=/dev/vda1 ro console=ttyS0,115200 quiet
Maintenance boot and bl-proof.service remain unchanged.

New sessions record boot metadata in session-runtime.json. Reopened sessions
require matching runtime version and fingerprints. Legacy or maintenance-created
sessions without that evidence retain GRUB; no automatic migration/reset was added.
Declared missing, unsafe/linked or changed assets fail preflight before VM start.

## Test correction and support limit

The exact shipped maintenance script established the EOF/default-Grok cause;
the corrected actual guest observation confirmed custom selection and imported
contents. This was a proof setup issue, not evidence of the app choosing the wrong
agent in its normal connected import path.

The first renewed attempt passed import and typing but stopped at an unrelated
SIZE-OK assertion. Policy2 has no SIZE-OK acknowledgement; it prints GUEST-SIZE
from the actual virtio terminal dimensions. The final attempt checks that real
GUEST-SIZE observation, keeps all required basic assertions and labels the newer
acknowledgement unsupported. No product protocol was changed or success faked.
Future runs refuse existing proof directories/disks, preserving retained work.

## Proof and timing

- Prior affected host batch: 25 passed, zero failures/skips. Product code is unchanged
  since that proof; the current change corrects the runner and stages actual assets.
- Real local bundle plan: complete backing chain and eight runtime inputs verified.
- Successful workflow: appResolvedLocalBundle, newSessionDirectBoot, import,
  terminal, customAgentSelected, guestResizeObserved, shutdown and
  reopenPersistedWork all true. No forced stop or workflow error.
- Both QEMU command receipts include the verified external kernel/initrd and quiet
  command line. Both serial logs contain clean Power down; process exit was zero.
- Prior three fresh-overlay timing: 6.5597 / 6.0177 / 6.0549 seconds to IMPORT-READY.
  Median 6.0549 s; range 0.5420 s. Historical baseline median 14.78 s. These timings
  were not repeated for the copied bundle and are not a universal startup guarantee.

Ignored detailed evidence: tests/LaunchPad.Tests/TestResults/direct-boot-finish.
The private final receipt points to successful/failed sessions and exact command,
serial and terminal logs. Original evidence is preserved under direct-boot.

## Still unproven

This pass used the actual app runtime resolver, boot selector, QEMU arguments,
import/status transport and clean-shutdown helper under the normal Windows user.
It did not exercise the physical Avalonia/Windows Terminal UI, BuildLaunchTest
launch credentials, vendor-agent authentication or host copy-back/agent /exit flow.
Those are separate acceptance limits, not failures observed in this direct-boot pass.
The missing newer SIZE-OK acknowledgement remains a guest capability limit outside
this direct-boot scope. No service fix, security scan, notification work or OpenVMM
implementation was performed. README remains owned separately and untouched.
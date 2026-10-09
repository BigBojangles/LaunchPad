# Release VM cleanup candidate

Branch: `clean-release-vm`. No commit, merge, push, installed handoff or release upload was performed. Existing unrelated licensing changes were preserved. This is a reviewed executable/runtime candidate, not a newly built installer.

## Selected artifacts

| Artifact | Selection / SHA-256 |
|---|---|
| Runtime manifest | `dist/images/runtime.json`, version `runtime-20261008-clean-locked` |
| Standalone image | `dist/images/debian-12-builder-runtime-20261008-clean-locked.qcow2` |
| Image SHA-256 | `afa43a1095f219de51cc542622d8b305dd291d475d7dea1dd54cf428a04078d0` |
| Image physical size | 1,208,877,056 bytes; virtual capacity 32 GiB |
| Maintenance archive | `dist/images/launchpad-maintenance-runtime-20261008-clean-locked.tar.gz` |
| Maintenance SHA-256 | `4bf29a32ee92983f24ba3556829a347432cb80d546358c5049cb65080cc6f741` |
| Review executable | `dist/LaunchPad.exe` |
| EXE SHA-256 | `28d67856ae0afa14dbaeee38c05277d5fc64d0135aba6f0c024ab880ba25cf81` |

The matching 6.1.0-53-amd64 kernel and initrd retain their old immutable filenames; their hashes are unchanged. The new image has no backing dependencies. The package plan selects six files: this image, kernel, initrd, maintenance archive and the two manifests. Older images remain preserved locally but are excluded from this selection. Saved user disks and outside installations were untouched.

## Approved removals and replacements

| Item | Result |
|---|---|
| Production supervisor | Replaced `bl-proof.sh` with `/usr/local/bin/launchpad-session`; production import, terminal, status, auth/history and return responsibilities retained. |
| Startup unit | Replaced `bl-proof.service` and its enabled link with `launchpad-session.service`; `Type=simple` represents the long-running supervisor. |
| Fake startup status | Removed unconditional `busy` / `needs-an-answer` sequence. This change does not establish automatic pager delivery from real events. |
| Experimental Windows bridge | Host no longer creates its serial channel or automatically connects a bridge broker. Image and maintenance reject/remove the guest command, broker/client, unit, enabled link and both known udev rule filenames, `70-` and `99-launchpad-windows-test.rules`. |
| Temporary construction files | Removed `/tmp/install-coding.sh`, inspection/repair reports, `builder.log` and `node-compile-cache`. |
| Provider installation caches | Removed builder/root npm caches and root-level Codex temporary wrapper state. Installed agents and development dependencies remain. |
| Package caches | Removed cached APT archives and fetched indexes; recreated required partial directories with appropriate ownership/mode. |
| Build logs and stale configuration | Removed approved apt/dpkg/alternatives logs, obsolete account backup files and the certificate configuration backup. Active account/security records remain. |
| Disk-persisted transient state | Removed approved `/run` adduser, reboot-required and blkid files from the underlying disk. |
| Machine identity | Empty template `/etc/machine-id`; dbus symlinks to it. Startup generates an identity. This is not a claim that systemd's `ConditionFirstBoot` is triggered. Existing session identities are preserved. |
| Builder password | Template construction runs `usermod -p '*' builder`, replacing the inherited hash with the exact locked marker; the release gate requires that marker. Password authentication is locked, not every mechanism for starting processes as this user. Saved-session accounts and maintenance are unchanged. |
| Maintenance consistency | New archive installs only the production supervisor/unit; old names occur solely in removal commands. It does not clear user home, credentials, project state or machine identity during upgrades. |

The cleanup boots only a fresh owned child overlay, with no network and a dedicated build-only maintenance transport. Originals are hash-verified before and after. Discard plus standalone conversion produces an independent output; the checks inspect allocated files, not every residual byte or deleted inode.

## End-of-phase proof

Evidence directory: `tests/LaunchPad.Tests/TestResults/guest-clean-build-20261008/` (ignored/private).

| Check | Result and evidence |
|---|---|
| Original preservation and clean construction | **TESTED â€” PASS.** `construction/build-state.json`, `build-console.log`; original three-image chain hashes unchanged, build VM exited without forced stop. |
| Standalone disk integrity | **TESTED â€” PASS.** QEMU image check after conversion; actual package plan confirms one disk with no backing. |
| Offline removal/unit/identity checks | **TESTED â€” PASS.** 51,117 entries inspected, 387 reviewed Debian/vendor name exceptions; final `package-preflight.log`. |
| Maintenance correspondence | **TESTED OFFLINE â€” PASS.** Exact image/helper/unit correspondence, source-bound apply script, full internal checksums, safe unique archive members and matching boot assets. Applying the final kit to an older saved VM is **UNTESTED**. |
| Gate rejection | **TESTED â€” PASS.** `negative-gate-checks.json`: rejected an actual stale maintenance kit, an existing file prohibited by a synthetic policy, and a changed content fingerprint for reviewed Debian `/usr/bin/test`. Synthetic policy checks did not mutate images. |
| Host regression coverage | **TESTED â€” PASS.** `host-cleanup.trx`: 25 checks; `standalone-activation-retry.trx`: one additional standalone activation/preservation assertion. The first added-test compile accidentally included private harness-generated C#; excluded that evidence from the test assembly, then passed the unchanged assertion. |
| Import and typing | **TESTED LIVE â€” PASS.** `basic-proof/workflow.json`: normal runtime resolver/QEMU/import/terminal transport with a disposable custom program. Imported content and binary payload verified intact; input reached the program. |
| Return and shutdown | **TESTED LIVE â€” PASS.** All three runs completed content return and normal machine shutdown. Native host conflict handling/AMSI/apply is outside this particular proof. |
| Persistence on reopen | **TESTED LIVE â€” PASS.** First run wrote work, second run reopened the same disk and verified it; returned work was intact. |
| Identity generalization | **TESTED LIVE â€” PASS.** Two fresh VMs had distinct IDs; reopening retained the first ID. |
| Custom selection and resize | **TESTED LIVE â€” PASS.** Explicit custom program launched; guest resize receipt observed. Historical `statusSizeReceipt` text in the original evidence JSON is stale fixture metadata; the actual `guestResizeObserved` result is authoritative. Fixture source corrected without duplicating the live run. |
| Executable publish | **TESTED â€” PASS.** `review-publish.log`; current EXE and affected checksum entries refreshed in `dist`. No GUI was opened automatically. |
| Installer package selection | **TESTED â€” PASS.** `runtime-payload-plan.json`: six selected runtime assets; no historical backing layer or session disk selected. |
| Installer compilation/install | **UNTESTED / NOT PERFORMED.** Full and Native installers/archives remain older. Corresponding-source and license provenance remains incomplete and independently blocks release packaging. |
| Real provider, GUI, pager acceptance | **UNTESTED AFTER CLEANUP.** No provider login, phone message, ordinary GUI acceptance or managed Windows test was attempted. |

**Locked-template follow-up â€” TESTED PASS:** the previous unlocked image is rejected by the new builder check. The rebuilt image passes the offline release check and actual six-file packaging preflight. Reusing the existing compiled disposable workflow against the locked image again passed import, terminal input, agent-user/custom-program launch, all three returns and normal shutdowns, reopened work, distinct fresh machine IDs and retained reopened identity. Evidence: `locked-basic-proof/workflow.json`, `locked-workflow.log`, `locked-package-preflight.log`. Unchanged host tests were not rerun; the EXE remains the prior reviewed build.

## Build and maintenance rules

`scripts/build-release-image.py` builds a fresh standalone candidate from pinned project-local inputs. `scripts/guest-release-clean.sh` is template-only and refuses known saved-project/provider state. It must never run against a user's session. Future template seeds still need provenance and privacy review.

`scripts/verify-release-image.py` fails on approved removed paths, custom leftover names, nonempty template identity, an unlocked or retained-hash builder password field, prefilled user/project/Git state, mismatched production source or stale maintenance. Legitimate Debian/vendor names containing `test`, `proof`, `probe` or `fixture` require exact reviewed content/link fingerprints in `release-image-policy.json`. Updates must deliberately review exceptions; the gate never automatically learns an allowlist from the candidate.

Install the pinned offline reader into `src/LaunchPad/obj/package/image-audit-tools` using `release-audit-requirements.txt`. Missing tools or failed checks block Full publish/installer planning. `package-runtime.ps1` runs the gate before producing a plan; Full installer source invokes it too. The direct installer compiler path is wired but was not executed in this phase.

## Privacy limits and follow-up

The allocated-file privacy scan read 41,266 regular files (3,257,764,371 bytes), with no read errors. It found no supplied personal email/project-path markers and no saved provider login/history, Git credentials, SSH key files or project files in user homes. Binary/doc string indicators are not proof of credentials: for example Codex contains concatenated token prefixes and surrounding static words that match a token regex. Pattern checks do not establish universal secret absence. Deleted-file recovery, secondary partitions, swap and older installer contents were not audited by this scan.

**Correction and resolution:** the original audited seed's builder account was not locked and had a password hash of unverified origin. KEEP/REMOVE item 8 now states that accurately. Casey then authorized replacing the inherited hash with `usermod -p '*' builder` in template construction. The newly selected `runtime-20261008-clean-locked` passed offline verification of the exact marker, and the previous unlocked image was rejected by the new gate. `locked-release-check.json`, `builder-unlocked-rejection.log`, `locked-package-preflight.log` and `locked-candidate-receipt.json` record this follow-up. The earlier privacy scan applies to the previous clean image; it is not a newly repeated universal privacy certification. Existing saved-session account records are unchanged.

Current tracked files exclude private settings, credentials, images, evidence and AGENTS.md. A subsequently approved source-privacy slice replaced identifying current path examples with profile/test-derived paths and added a build/publish gate; see `docs/source-privacy.md`. The reachable Git history still contains older AGENTS.md, identifying example paths and commit-author email metadata. No credential-pattern matches were found in 891 inspected history blobs across 20 reachable commits; this is a bounded local history check, not remote-only/unreachable-object or arbitrary-secret proof. Detailed findings remain in ignored private evidence. Neither `.gitignore` nor deleting a current file erases historical commits. No history rewrite was authorized or performed.

## Packaging follow-up: 1.0.2

The earlier installer status above describes the cleanup phase, before packaging.
The Full installer is now compiled from the selected clean locked runtime.
`LaunchPad-Setup.exe`, `LaunchPad-Setup-1.bin` and `LaunchPad-Setup-2.bin` use
lzma2 compression with 1 GiB slice limits. No old runtime backing layer or saved
session was selected. Native and CLI packages were not rebuilt in this pass.

QEMU Fence and the rebuilt `qemu-img` both use the verified 11.1.50 patched tree.
The new utility passed disposable disk and overlay create/check/backing-chain
proofs before replacing the old utility; only seven required DLLs remain beside
it. Twenty obsolete vendor DLLs were removed. Original build sources were read,
not modified; the authorized temporary WSL mount was removed after building.

`LaunchPad-1.0.2-sources.zip` contains the exact patched tree, console-size mbox,
cross/build configuration and corresponding GLib, libiconv and libintl source
packages/recipes. `licenses/manifest-full.json` and `THIRD-PARTY-NOTICES.txt`
record 44 components; `licenses/debian-packages.json` records 418 guest packages.
The licensing and clean-image preflights, source-privacy gate, final asset
checksums, source ZIP CRC and permitted QEMU EXE inventory passed. Private
evidence is in the existing `TestResults/package-refresh-20261008/` folder:
`full-publish.log`, `final-package-proof.json`, and
`source-inputs/qemu-img-provenance.json`.

Installer execution and physical acceptance remain untested. The optional
Git-config credential sanitation was deferred: safely changing real import
hashes and recovery rules requires its own reviewed slice. The first SDK run
reported development-certificate creation; future packaging explicitly disables
that behavior, and no certificate-store deletion was attempted. README and Git
history were unchanged; no commit, push or release upload occurred.

Next action: Casey reviews the compiled package and existing identifying-history
findings. Publish the sources ZIP alongside the installer if this candidate is
released. No additional implementation or repeated proof is selected automatically.

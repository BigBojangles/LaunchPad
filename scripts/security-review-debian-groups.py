#!/usr/bin/env python3
"""Join a preserved review queue to official vendor data; never waive findings."""
import argparse
import csv
import hashlib
import json
from collections import Counter
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def packages(path):
    result = {}
    for paragraph in path.read_text().split('\n\n'):
        fields = {}
        for line in paragraph.splitlines():
            if line and not line[0].isspace() and ': ' in line:
                key, value = line.split(': ', 1)
                fields[key] = value
        if fields.get('Status') == 'install ok installed':
            result[fields['Package']] = fields
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--review-root', type=Path, required=True)
    parser.add_argument('--queue', type=Path, required=True)
    parser.add_argument('--runtime-witness', type=Path, required=True)
    parser.add_argument('--runtime-probe', type=Path, required=True)
    parser.add_argument('--candidate-sha256', required=True)
    args = parser.parse_args()
    project = Path(__file__).resolve().parent.parent
    generated = project / 'tests/LaunchPad.Tests/TestResults'
    root = args.review_root.resolve()
    if not root.is_relative_to(generated / 'security'):
        raise ValueError('Review must remain in private security output.')
    queue = args.queue.resolve()
    if not queue.is_relative_to(generated / 'security'):
        raise ValueError('Queue must remain in private security output.')
    output = root / 'debian-source-review-private.json'
    reviewed_queue = root / 'high-critical-reviewed-private.csv'
    if output.exists() or reviewed_queue.exists():
        raise ValueError('Retain completed review; choose a new review directory.')
    vendor_file = root / 'debian-tracker.json'
    download_file = root / 'vendor-download-private.json'
    download = json.loads(download_file.read_text(encoding='utf-8-sig'))
    if download['sourceUri'] != 'https://security-tracker.debian.org/tracker/data/json':
        raise ValueError('Use the official Debian tracker.')
    assert download['httpStatus'] == 200 and sha(vendor_file) == download['sha256']
    evidence_root = root / 'completed/rootfs'
    evidence_file = evidence_root / 'evidence-private.json'
    evidence = json.loads(evidence_file.read_text())
    assert evidence['candidateSha256'] == args.candidate_sha256
    assert evidence['readOnly'] and evidence['candidateUnchanged']
    for name, digest in evidence['files'].items():
        assert sha(evidence_root / name) == digest
    installed = packages(evidence_root / 'package-status.txt')
    probe = json.loads(args.runtime_probe.read_text(encoding='utf-8-sig'))
    assert probe['templateSha256'] == args.candidate_sha256
    assert probe['guestShutdownObserved'] and probe['auditFinishedMarkerObserved']
    boot = args.runtime_witness.read_text().strip()
    assert '6.1.0-53-amd64' in boot and 'Debian 6.1.187-1' in boot
    assert installed['linux-image-6.1.0-53-amd64']['Version'] == '6.1.187-1'
    assert installed['linux-image-6.1.0-53-amd64']['Built-Using'] == 'linux (= 6.1.187-1)'
    vendor = json.loads(vendor_file.read_text())
    with queue.open(encoding='utf-8-sig', newline='') as stream:
        rows = list(csv.DictReader(stream))
    groups = {}
    for row in rows:
        source = ('vim' if row['package'] in ('vim', 'vim-common', 'vim-runtime', 'vim-tiny')
                  else 'linux' if row['package'] == 'linux-libc-dev' else None)
        if source is None:
            continue
        assert installed[row['package']]['Version'] == row['installedVersion']
        key = (source, row['vulnerabilityId'])
        if key not in groups:
            finding = vendor.get(source, {}).get(row['vulnerabilityId'])
            release = (finding or {}).get('releases', {}).get('bookworm', {})
            # Open vendor issues stay blocking. Do not infer exploitability, fixes,
            # or not-affected from scores, package headers, or missing fixes.
            decision = ('affected-vendor-tracked' if source == 'vim' and
                        release.get('status') == 'open' else 'applicability-pending')
            groups[key] = dict(source=source, vulnerabilityId=row['vulnerabilityId'],
                sourceUri='https://security-tracker.debian.org/tracker/'+row['vulnerabilityId'],
                vendorRecord=finding, bookworm=release, packages=[], rowCount=0,
                proposedDisposition=decision,
                reason=('Exact installed Vim family matches an open Bookworm issue; retain as affected pending remediation. '
                        'This conservative source-level verdict does not prove exploitability or trigger reachability in each binary.'
                        if source == 'vim' else
                        'Headers are not executable kernel coverage. Bound running-kernel version/config to the signed package Built-Using source; '
                        'per-CVE backport, enabled feature/module, privilege and trigger review remains required.'))
        group = groups[key]
        group['rowCount'] += 1
        group['packages'].append(dict(name=row['package'], version=row['installedVersion'],
                                      originalDisposition=row['disposition']))
    reviewed_rows = [dict(row) for row in rows]
    for row in reviewed_rows:
        source = ('vim' if row['package'] in ('vim', 'vim-common', 'vim-runtime', 'vim-tiny')
                  else 'linux' if row['package'] == 'linux-libc-dev' else None)
        if source is None or row['disposition'] != 'unreviewed':
            continue
        group = groups[(source, row['vulnerabilityId'])]
        row['disposition'] = group['proposedDisposition']
        row['applicabilityEvidence'] = (
            str(output)+'; '+group['sourceUri']+'; '+group['reason'])
        row['remediation'] = (
            'Prepare tested backports or a compatible maintained Vim build covering the vendor issues; '
            'verify editors and agent tooling before candidate activation. No Bookworm fix or exception claimed.'
            if source == 'vim' else
            'Complete per-CVE running-kernel applicability/backport/config/module/privilege review; '
            'remediate applicable issues with a tested supported kernel. Headers alone do not close kernel coverage.')
    # A new phase queue preserves the original queue and its historical proof hashes.
    with reviewed_queue.open('x', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(reviewed_rows)
    result = dict(schema=1, candidateSha256=args.candidate_sha256,
        candidateSelected=False, scannerRerun=False, imageModified=False,
        queueModified=False, riskExceptions=0, vendorDownload=download,
        inputQueue=dict(path=str(queue), sha256=sha(queue), rows=len(rows),
                        dispositions=dict(Counter(r['disposition'] for r in rows))),
        reviewedQueue=dict(path=str(reviewed_queue), sha256=sha(reviewed_queue),
                           rows=len(reviewed_rows), originalQueuePreserved=True,
                           dispositions=dict(Counter(r['disposition'] for r in reviewed_rows)),
                           unresolvedRows=sum(r['disposition'] != 'not-affected' for r in reviewed_rows)),
        runtimeWitness=dict(path=str(args.runtime_witness), sha256=sha(args.runtime_witness),
                            text=boot, probePath=str(args.runtime_probe), probeSha256=sha(args.runtime_probe)),
        packageEvidence=dict(path=str(evidence_file), sha256=sha(evidence_file)),
        installedKernelPackages={name: fields for name, fields in installed.items()
                                 if name.startswith('linux-image') or name == 'linux-libc-dev'},
        groups=list(groups.values()),
        summary=dict(groups=len(groups), rows=sum(g['rowCount'] for g in groups.values()),
                     proposedDispositionRows=dict(Counter(g['proposedDisposition']
                                                         for g in groups.values()
                                                         for _ in range(g['rowCount'])))),
        verdict='BLOCKED', limitations='Official vendor/source-level grouped review, not a new scanner verdict, exploitation test or waiver. '
        'No transfer to selected production image; native bundled agent dependencies and complete kernel applicability remain separate. '
        'Historical scan/triage summaries are preserved.')
    output.write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(result['summary'], indent=2))


if __name__ == '__main__':
    main()

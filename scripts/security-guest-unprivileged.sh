#!/bin/sh
# Diagnostic program supplied outside the template. Run by the production guest
# launcher as builder, without sudo. Raw output belongs only in private reports.
set -u
printf '\nLP-PROBE-BEGIN\n'
printf 'IDENTITY:'; id
printf 'PROFILE:'; cat /proc/self/attr/current 2>/dev/null || true
printf 'PROJECT:'; cat baseline-input.txt
printf 'PROJECT-WRITE:'; printf 'probe\n' > baseline-output.txt && echo OK
if [ "$(id -u)" != 1000 ]; then
    printf 'SECURITY-WRONG-IDENTITY\nLP-PROBE-END\n'
    exit 1
fi
printf 'LINPEAS-SHA256:'; sha256sum linpeas.sh
if ! printf '%s  linpeas.sh\n' '7454e8f3fc817fdb7de0818ac7b80a05071ebf4194f7a6e13689985033593870' | sha256sum -c -; then
    printf 'SECURITY-HASH-FAILED\nLP-PROBE-END\n'
    exit 1
fi
printf 'LINPEAS-BEGIN\n'
# This release advertises -n but omits it from getopts. Its host-checker URL
# override keeps the default system-information request on guest loopback.
# Exclude network_information (which probes public Internet endpoints) and cloud
# metadata. No discovery, brute force or online package lookup is requested.
export HACKTRICKS_HOST_CHECKER_URL=http://127.0.0.1:1
printf 'LOCAL-NETWORK-BEGIN\n'
ip address 2>/dev/null || true
ip route 2>/dev/null || true
ss -lnt 2>/dev/null || true
printf 'LOCAL-NETWORK-END\n'
timeout 420 sh ./linpeas.sh -N -o system_information,container,procs_crons_timers_srvcs_sockets,users_information,software_information,interesting_perms_files,interesting_files,api_keys_regex
result=$?
printf '\nLINPEAS-EXIT:%s\n' "$result"
printf 'LINPEAS-END\nLP-PROBE-END\n'
exit "$result"

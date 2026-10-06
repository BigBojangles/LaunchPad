#!/bin/sh
# Separate pinned audit source. No install, sudo, NSE or network discovery.
set -u
printf '\nLP-PROBE-BEGIN\n'
trap 'sync; printf "LP-PROBE-END\n"' EXIT
printf 'IDENTITY:'; id
printf 'PROFILE:'; cat /proc/self/attr/current 2>/dev/null || true
printf 'PROJECT:'; cat baseline-input.txt
printf 'PROJECT-WRITE:'; printf 'probe\n' > baseline-output.txt && echo OK
if [ "$(id -u)" != 1000 ]; then printf 'NMAP-WRONG-IDENTITY\n'; exit 1; fi
if ! printf '%s  nmap-7.991.tar.bz2\n' 'a5d507f29437bef3bedd4771ff9aaa8fc1c2a109ddba1f5b1cf12027456929be' | sha256sum -c -; then
    printf 'NMAP-PIN-FAILED\n'; exit 1
fi
printf 'COMPILER:'; g++ --version | head -n 1
mkdir .lp-nmap-build || exit 1
tar -xjf nmap-7.991.tar.bz2 --strip-components=1 -C .lp-nmap-build || exit 1
(
    cd .lp-nmap-build || exit 1
    # 7.991's --without-liblua build fails on an unguarded close_nse call.
    # Keep the official source unchanged; no NSE options are used in our scans.
    timeout 180 ./configure --without-zenmap --without-ndiff --without-ncat --without-nping --with-liblua=included --with-libpcap=included --with-libpcre=included > configure-private.log 2>&1 || exit 1
    timeout 900 make -j2 nmap > make-private.log 2>&1
)
result=$?
printf 'NMAP-BUILD-EXIT:%s\n' "$result"
if [ "$result" != 0 ]; then
    tail -n 12 .lp-nmap-build/make-private.log
    exit "$result"
fi
printf 'NMAP-BINARY-SHA256:'; sha256sum .lp-nmap-build/nmap
.lp-nmap-build/nmap --version
python3 security-nmap-probe.py
result=$?
printf 'NMAP-PROBE-EXIT:%s\n' "$result"
# Preserve generated tool/probe evidence for offline review after VM cleanup.
exit "$result"

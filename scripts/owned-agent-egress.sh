#!/bin/sh
# Fixture-only guard: no public traffic, without replacing the production
# gateway rule. The one owned gateway target falls through to normal rules.
set -eu
test "$(id -u)" = 0
port=$(python3 -c 'import json; p=json.load(open("/opt/launchpad-policy-probe/owned_targets.json"))["hostPort"]; assert isinstance(p,int) and 1024<=p<=65535; print(p)')
iptables -N LP_OWNED_TOOL
iptables -A LP_OWNED_TOOL -o lo -j RETURN
iptables -A LP_OWNED_TOOL -d 10.0.2.2 -p tcp --dport "$port" -j RETURN
iptables -A LP_OWNED_TOOL -j REJECT
iptables -A OUTPUT -m owner --uid-owner 1000 -j LP_OWNED_TOOL
printf 'Fixture-only owner egress guard installed.\n'

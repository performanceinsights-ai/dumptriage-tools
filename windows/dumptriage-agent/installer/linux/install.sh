#!/usr/bin/env bash
# Installs the DumpTriage agent as a headless systemd service (no tray app).
# Usage: sudo ./install.sh <published-agent-dir> [-]
#   Publish first: dotnet publish src/DumpTriageAgent -c Release -r linux-x64 --self-contained -o publish/agent
#   Pass "-" as the 2nd arg to read the API key from stdin (never from argv).
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 1; }
[[ $# -ge 1 && -x $1/DumpTriageAgent ]] || { echo "usage: $0 <published-agent-dir> [-]" >&2; exit 1; }

src=$1
dest=/opt/dumptriage/agent
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

systemctl stop dumptriage-agent 2>/dev/null || true
install -d -m 0755 "$dest"
cp -a "$src"/. "$dest"/
install -d -m 0755 /etc/dumptriage /var/lib/dumptriage/incoming
install -m 0644 "$here/dumptriage-agent.service" /etc/systemd/system/dumptriage-agent.service

if [[ ${2:-} == "-" ]]; then
  "$dest/DumpTriageAgent" --set-api-key-stdin
elif [[ ! -f /etc/dumptriage/apikey ]]; then
  echo "warning: no API key set; run: echo <key> | $dest/DumpTriageAgent --set-api-key-stdin" >&2
fi

systemctl daemon-reload
systemctl enable --now dumptriage-agent
echo "Installed. Config: /etc/dumptriage/config.json (restart the service after edits). Logs: journalctl -u dumptriage-agent"

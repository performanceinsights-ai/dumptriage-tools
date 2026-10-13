#!/usr/bin/env bash
# Removes the service and binaries. Config, API key and queue are kept unless --purge.
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 1; }

systemctl disable --now dumptriage-agent 2>/dev/null || true
rm -f /etc/systemd/system/dumptriage-agent.service
systemctl daemon-reload
rm -rf /opt/dumptriage
if [[ ${1:-} == "--purge" ]]; then
  rm -rf /etc/dumptriage /var/lib/dumptriage /var/log/dumptriage
fi
echo "Uninstalled."

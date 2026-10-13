#!/usr/bin/env bash
# Removes the DumpTriage Linux agent installed by install.sh (service, cron job, binaries).
#   installer/uninstall.sh [--remove-data]    # --remove-data also deletes config, API key, queue and logs
set -euo pipefail
remove_data=0
[[ ${1:-} == --remove-data ]] && remove_data=1

if ((EUID == 0)); then
  lib=/usr/local/lib/dumptriage bin=/usr/local/bin cfg=/etc/dumptriage state=/var/lib/dumptriage
  unit=/etc/systemd/system/dumptriage-agent.service sctl=(systemctl)
else
  lib=$HOME/.local/lib/dumptriage bin=$HOME/.local/bin cfg=${XDG_CONFIG_HOME:-$HOME/.config}/dumptriage
  state=${XDG_STATE_HOME:-$HOME/.local/state}/dumptriage
  unit=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/dumptriage-agent.service sctl=(systemctl --user)
fi

if [[ -f $unit ]]; then
  "${sctl[@]}" disable --now dumptriage-agent.service 2>/dev/null || true
  rm -f "$unit"
  "${sctl[@]}" daemon-reload 2>/dev/null || true
  echo "removed service"
fi
rm -f /etc/cron.d/dumptriage-agent 2>/dev/null || true
if command -v crontab >/dev/null && crontab -l 2>/dev/null | grep -q '# dumptriage-agent$'; then
  { crontab -l | grep -v '# dumptriage-agent$' || true; } | crontab -
  echo "removed cron job"
fi
rm -rf "$lib"
rm -f "$bin/dumptriage-agent"
if ((remove_data)); then rm -rf "$cfg" "$state"; echo "removed $cfg and $state"
else echo "kept $cfg and $state (use --remove-data to delete)"; fi

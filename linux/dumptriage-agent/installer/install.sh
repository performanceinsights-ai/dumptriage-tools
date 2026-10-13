#!/usr/bin/env bash
# Installs the DumpTriage Linux agent as a background service.
#
#   installer/install.sh [--api-key-file FILE | --api-key-stdin] [--policy MODE]
#                        [--api-url URL] [--watch PATH]... [--method METHOD] [--dry-run]
#
# Method (default auto):
#   systemd-system  root, systemd present: system service (reads root-only dumps such as /var/crash)
#   systemd-user    non-root, user systemd present: per-user service, lingering enabled so it runs without a login
#   cron            no systemd: a once-a-minute `run --once` job guarded by flock
# auto picks systemd-system as root, systemd-user as a normal user with a working user manager, else cron.
#
# The API key (an upload-scoped dts_ key) is read from --api-key-file (deleted once stored, like the
# Windows installer) or stdin; never from the command line. Omit it on a reinstall to keep the old one.
# Re-running upgrades the agent and keeps config.json, changing only the fields given as options.
set -euo pipefail

policy="" api_url="" method=auto key_file="" key_stdin=0 dry=0
watch=()
here=$(cd "$(dirname "$0")" && pwd)
agent_src=$here/../dumptriage_agent.py

die() { echo "install.sh: $*" >&2; exit 1; }
run() { if ((dry)); then echo "+ $*"; else "$@"; fi; }

while (($#)); do
  case $1 in
    --api-key-file) key_file=${2:?}; shift 2 ;;
    --api-key-stdin) key_stdin=1; shift ;;
    --policy) policy=${2:?}; shift 2 ;;
    --api-url) api_url=${2:?}; shift 2 ;;
    --watch) watch+=("${2:?}"); shift 2 ;;
    --method) method=${2:?}; shift 2 ;;
    --dry-run) dry=1; shift ;;
    -h | --help) sed -n '2,/^set -e/p' "$0" | sed '$d;s/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done
case $policy in "" | AlwaysSubmit | AskFirst | NotifyOnly | RuleBased) ;; *) die "bad --policy $policy" ;; esac
[[ -f $agent_src ]] || die "cannot find $agent_src"
command -v python3 >/dev/null || die "python3 is required"
python3 -c 'import sys; sys.exit(sys.version_info < (3, 8))' || die "python 3.8+ is required"

if ((EUID == 0)); then
  lib=/usr/local/lib/dumptriage bin=/usr/local/bin cfg=/etc/dumptriage state=/var/lib/dumptriage
  unit_dir=/etc/systemd/system sctl=(systemctl)
else
  lib=$HOME/.local/lib/dumptriage bin=$HOME/.local/bin cfg=${XDG_CONFIG_HOME:-$HOME/.config}/dumptriage
  state=${XDG_STATE_HOME:-$HOME/.local/state}/dumptriage
  unit_dir=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user sctl=(systemctl --user)
fi
agent=$lib/dumptriage_agent.py
cli=$bin/dumptriage-agent

if [[ $method == auto ]]; then
  if ((EUID == 0)) && [[ -d /run/systemd/system ]]; then method=systemd-system
  elif ((EUID != 0)) && systemctl --user show-environment >/dev/null 2>&1; then method=systemd-user
  else method=cron; fi
fi
case $method in
  systemd-system) ((EUID == 0)) || die "systemd-system needs root" ;;
  systemd-user) ((EUID != 0)) || die "systemd-user is for a normal user (run without sudo)" ;;
  cron) command -v crontab >/dev/null || [[ -d /etc/cron.d ]] || die "cron is not installed (apt install cron / dnf install cronie)" ;;
  *) die "unknown --method $method" ;;
esac
echo "Installing DumpTriage agent (method: $method)"

run mkdir -p "$lib" "$bin" "$cfg" "$state"
run chmod 700 "$cfg" "$state"
run install -m 0755 "$agent_src" "$agent"
run ln -sf "$agent" "$cli"
# Wrapper so every invocation uses this install's config/state, whoever runs it.
if ((!dry)); then
  printf '#!/bin/sh\nexec python3 %q --config-dir %q --state-dir %q "$@"\n' "$agent" "$cfg" "$state" >"$cli.tmp"
  chmod 0755 "$cli.tmp" && mv -f "$cli.tmp" "$cli"
fi

# API key: file (then deleted) or stdin.
if [[ -n $key_file ]]; then
  [[ -f $key_file ]] || die "no such file $key_file"
  if ((dry)); then echo "+ store key from $key_file"; else "$cli" set-api-key <"$key_file"; rm -f "$key_file"; fi
elif ((key_stdin)); then
  ((dry)) || "$cli" set-api-key
elif [[ ! -f $cfg/api-key ]]; then
  echo "warning: no API key given on first install; run '$cli set-api-key' (key on stdin) before dumps can upload" >&2
fi

# Config: keep what exists, set only the fields given.
if ((!dry)); then
  python3 - "$cfg/config.json" "$policy" "$api_url" "${watch[@]+"${watch[@]}"}" <<'PY'
import json, os, sys
path, policy, api_url, *watch = sys.argv[1:]
cfg = json.load(open(path)) if os.path.exists(path) else {}
if policy:
    cfg.setdefault("policy", {})["mode"] = policy
elif "policy" not in cfg:
    cfg["policy"] = {"mode": "AskFirst"}
if api_url:
    cfg["apiBaseUrl"] = api_url
if watch:
    cfg["watchedPaths"] = watch
elif "watchedPaths" not in cfg and os.geteuid() != 0:
    # A normal user cannot read /var/crash or the systemd-coredump store; watch the shared
    # directory that linux/configure-dumps.sh sets up (cores there stay owned by their crasher).
    cfg["watchedPaths"] = ["/var/crash/dumptriage"]
json.dump(cfg, open(path, "w"), indent=2)
PY
fi

case $method in
  systemd-system | systemd-user)
    unit=$unit_dir/dumptriage-agent.service
    run mkdir -p "$unit_dir"
    if ((!dry)); then
      cat >"$unit" <<UNIT
[Unit]
Description=DumpTriage agent (watches for crash dumps and submits them to DumpTriage)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=$cli run
Restart=always
RestartSec=10
Nice=10
IOSchedulingClass=idle
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=$([[ $method == systemd-system ]] && echo multi-user.target || echo default.target)
UNIT
    fi
    run "${sctl[@]}" daemon-reload
    run "${sctl[@]}" enable dumptriage-agent.service
    run "${sctl[@]}" restart dumptriage-agent.service
    if [[ $method == systemd-user ]]; then
      run loginctl enable-linger "$USER" || echo "warning: could not enable lingering; the agent will only run while you are logged in (sudo loginctl enable-linger $USER)" >&2
    fi
    ;;
  cron)
    line="* * * * * flock -n $state/cron.lock $cli run --once >>$state/cron.log 2>&1"
    if ((EUID == 0)) && [[ -d /etc/cron.d ]]; then
      run bash -c "printf '# DumpTriage agent\nSHELL=/bin/sh\nPATH=/usr/local/bin:/usr/bin:/bin\n* * * * * root flock -n $state/cron.lock $cli run --once >>$state/cron.log 2>&1\n' > /etc/cron.d/dumptriage-agent"
    else
      if ((!dry)); then
        { crontab -l 2>/dev/null | grep -v '# dumptriage-agent$' || true; echo "$line # dumptriage-agent"; } | crontab -
      else echo "+ add to crontab: $line"; fi
    fi
    ;;
esac

echo "Done. Config: $cfg/config.json (policy: ${policy:-as configured}). Status: $cli status"

#!/usr/bin/env bash
# Installs the DumpTriage Linux agent as a background service.
#
#   installer/install.sh [--api-key-file FILE | --api-key-stdin] [--policy MODE]
#                        [--api-url URL] [--watch PATH]... [--method METHOD] [--with-perfetto] [--dry-run]
#
# --with-perfetto also installs `tracebox` (Perfetto's all-in-one recorder) beside the agent, for tracing.backend "perfetto":
#   the release pinned in installer/perfetto.pin, downloaded over HTTPS and refused unless its SHA-256 is the one kept in that
#   file. The default install needs only the distro's `perf` (see linux/configure-tracing.sh).
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

policy="" api_url="" method=auto key_file="" key_stdin=0 dry=0 with_perfetto=0
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
    --with-perfetto) with_perfetto=1; shift ;;
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
# The flight-recorder config the perfetto backend fills in; found next to the agent.
if [[ -f $here/../perfetto/flight-recorder.pbtx ]]; then
  run mkdir -p "$lib/perfetto"
  run install -m 0644 "$here/../perfetto/flight-recorder.pbtx" "$lib/perfetto/flight-recorder.pbtx"
fi
# Wrapper so every invocation uses this install's config/state, whoever runs it.
if ((!dry)); then
  printf '#!/bin/sh\nexec python3 %q --config-dir %q --state-dir %q "$@"\n' "$agent" "$cfg" "$state" >"$cli.tmp"
  chmod 0755 "$cli.tmp" && mv -f "$cli.tmp" "$cli"
fi

# tracebox, pinned and checksummed (--with-perfetto).
install_tracebox() {
  local pin=${DT_PERFETTO_PIN:-$here/perfetto.pin} arch url want got
  [[ -f $pin ]] || die "cannot find $pin"
  case $(uname -m) in
    x86_64 | amd64) arch=amd64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) die "no pinned tracebox for $(uname -m) (amd64 and arm64 only)" ;;
  esac
  url=$(sed -n "s/^url_$arch=//p" "$pin")
  want=$(sed -n "s/^sha256_$arch=//p" "$pin")
  [[ -n $url && $want =~ ^[0-9a-f]{64}$ ]] || die "$pin has no usable url_$arch and sha256_$arch"
  if ((dry)); then echo "+ download $url, require sha256 $want, install tracebox into $lib"; return; fi
  tmp=$(mktemp -d)  # global, removed by the EXIT trap, which also covers die
  trap '[[ -z ${tmp:-} ]] || rm -rf "$tmp"' EXIT
  echo "Downloading tracebox ($(sed -n 's/^version=//p' "$pin"), $arch) ..."
  if command -v curl >/dev/null; then curl -fsSL --max-time 300 -o "$tmp/perfetto.zip" "$url" || die "download of $url failed"
  elif command -v wget >/dev/null; then wget -q -T 300 -O "$tmp/perfetto.zip" "$url" || die "download of $url failed"
  else die "curl or wget is required for --with-perfetto"; fi
  if command -v sha256sum >/dev/null; then got=$(sha256sum "$tmp/perfetto.zip" | cut -d' ' -f1); else got=$(shasum -a 256 "$tmp/perfetto.zip" | cut -d' ' -f1); fi
  [[ $got == "$want" ]] || die "the download's SHA-256 is $got, not the pinned $want: not installing it"
  python3 - "$tmp/perfetto.zip" "$tmp" <<'PY' || die "the archive has no tracebox"
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
name = next(n for n in z.namelist() if n.rsplit("/", 1)[-1] == "tracebox")
open(sys.argv[2] + "/tracebox", "wb").write(z.read(name))
PY
  chmod 0755 "$tmp/tracebox"
  "$tmp/tracebox" --version >/dev/null 2>&1 || die "the downloaded tracebox does not run on this host"
  install -m 0755 "$tmp/tracebox" "$lib/tracebox"
  echo "tracebox installed at $lib/tracebox: $("$lib/tracebox" --version 2>&1 | head -1)"
}
if ((with_perfetto)); then install_tracebox; fi

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

#!/usr/bin/env bash
# Prepares a Linux host for DumpTriage performance traces (website#1999), the counterpart of configure-dumps.sh.
#
#   sudo linux/configure-tracing.sh [--agent-user USER] [--dry-run]         install perf, set what the agent's user needs
#   sudo linux/configure-tracing.sh --check [--check-frame-pointers]        change nothing; report what is and is not ready
#   sudo linux/configure-tracing.sh --capture SECONDS [--output-dir DIR]    one manual capture, no agent needed
#   sudo linux/configure-tracing.sh --undo [--dry-run]                      remove the sysctl file written here
#
# Idempotent. What it does, and only this:
#   - installs perf with the distro's package manager (never snap) if `perf --version` does not work. On Ubuntu a
#     `perf` wrapper that says "perf not found for kernel ..." counts as not installed.
#   - the agent normally runs as root (the system install), and then no setting is changed. With --agent-user USER
#     (an unprivileged account) it writes /etc/sysctl.d/60-dumptriage-tracing.conf with kernel.perf_event_paranoid = 0
#     and kernel.kptr_restrict = 0, which system-wide perf and kernel symbols need for a non-root user. Both
#     loosen the host for every local user: prefer the root agent. --undo removes the file (the live values stay
#     until the next boot or until you set them).
#   - reports whether tracefs is available (software-event profiling does not need it; sched tracepoints do).
#   - --check-frame-pointers samples the whole system for 3 s and warns about processes whose stacks are mostly
#     [unknown]: a binary built without frame pointers gives truncated stacks under `perf record -g`.
#   - --capture SECONDS (30 to 600): `perf record -F 99 -a -g` for that long (at 99 Hz, 30 s is about 3,000 samples
#     per busy core; the service needs 2,000 to judge a profile, website#2208), `perf script --header` with home directories and
#     the host name replaced, a manifest.json, and the curl command that uploads both to POST /api/v1/traces.
# Test hooks (environment): DT_OS_RELEASE, DT_SYSCTL_D, DT_PROC_SYS, DT_TRACEFS.
set -euo pipefail

agent_user=root check=0 fp=0 dry=0 undo=0 capture="" outdir=.
while (($#)); do
  case $1 in
    --agent-user) agent_user=${2:?}; shift 2 ;;
    --check) check=1; shift ;;
    --check-frame-pointers) fp=1; shift ;;
    --capture) capture=${2:?}; shift 2 ;;
    --output-dir) outdir=${2:?}; shift 2 ;;
    --undo) undo=1; shift ;;
    --dry-run) dry=1; shift ;;
    -h | --help) sed -n '2,/^set -e/p' "$0" | sed '$d;s/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -z $capture || $capture =~ ^[0-9]+$ && $capture -ge 30 && $capture -le 600 ]] || { echo "--capture takes 30 to 600 seconds (shorter is too thin to judge)" >&2; exit 2; }
[[ $agent_user =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "--agent-user: not a user name" >&2; exit 2; }

tmp=""  # a scratch directory, removed on any exit
trap '[[ -z $tmp ]] || rm -rf "$tmp"' EXIT

os_release=${DT_OS_RELEASE:-/etc/os-release}
sysctl_d=${DT_SYSCTL_D:-/etc/sysctl.d}
proc_sys=${DT_PROC_SYS:-/proc/sys}
sysctl_conf=$sysctl_d/60-dumptriage-tracing.conf

run() { if ((dry)); then echo "+ $*"; else "$@"; fi; }
say() { echo "$*"; }
warn() { echo "warning: $*" >&2; }
need_root() { ((EUID == 0 || dry)) || { echo "run as root (sudo)" >&2; exit 1; }; }

perf_works() { command -v perf >/dev/null 2>&1 && perf --version >/dev/null 2>&1; }

distro_id() {
  local id="" like=""
  if [[ -r $os_release ]]; then
    # shellcheck disable=SC1090
    id=$(. "$os_release" && echo "${ID:-}") like=$(. "$os_release" && echo "${ID_LIKE:-}")
  fi
  echo "$id $like"
}

install_perf() {
  local ids
  ids=$(distro_id)
  case " $ids " in
    *" ubuntu "*) run apt-get install -y linux-tools-common "linux-tools-$(uname -r)" || run apt-get install -y linux-tools-generic ;;
    *" debian "*) run apt-get install -y linux-perf ;;
    *" rhel "* | *" fedora "* | *" centos "* | *" rocky "* | *" almalinux "* | *" ol "*)
      if command -v dnf >/dev/null 2>&1; then run dnf install -y perf; else run yum install -y perf; fi ;;
    *" suse "* | *" sles "* | *" opensuse-leap "*) run zypper --non-interactive install perf ;;
    *" arch "*) run pacman -S --noconfirm perf ;;
    *" alpine "*) run apk add perf ;;
    *) echo "unknown distribution ($ids): install the package that provides the perf command, then run this again" >&2; return 1 ;;
  esac
}

report_perf() {
  if perf_works; then say "perf: $(perf --version 2>&1 | head -1)"; else say "perf: not usable"; return 1; fi
}

paranoid() { cat "$proc_sys/kernel/perf_event_paranoid" 2>/dev/null || echo "?"; }

report_privileges() {
  say "kernel.perf_event_paranoid: $(paranoid); kernel.kptr_restrict: $(cat "$proc_sys/kernel/kptr_restrict" 2>/dev/null || echo '?')"
  if [[ $agent_user == root ]]; then
    say "agent user: root, so system-wide perf works whatever perf_event_paranoid says"
  fi
}

report_tracefs() {
  local t="" c
  for c in ${DT_TRACEFS:-/sys/kernel/tracing /sys/kernel/debug/tracing}; do
    if [[ -d $c && -r $c/available_events ]]; then t=$c; break; fi
  done
  if [[ -n $t ]]; then
    say "tracefs: $t"
  else
    say "tracefs: not readable here (CPU sampling works without it; sched tracepoint captures need it)"
  fi
}

frame_pointer_check() {
  perf_works || { warn "perf is not usable: cannot check frame pointers"; return 0; }
  tmp=$(mktemp -d)
  say "sampling the whole system for 3 s ..."
  if ! perf record -q -F 199 -a -g -o "$tmp/p.data" -- sleep 3 >/dev/null 2>"$tmp/err"; then
    warn "perf record failed: $(tr '\n' ' ' <"$tmp/err" | cut -c1-200)"
    return 0
  fi
  perf script -f -i "$tmp/p.data" -F comm,ip,sym 2>/dev/null | awk '
    /^[^ \t]/ { if (comm != "") { n[comm]++; if (bad) u[comm]++ } comm = $1; bad = 0; next }
    /\[unknown\]/ { bad = 1 }
    END { if (comm != "") { n[comm]++; if (bad) u[comm]++ }
          for (c in n) if (n[c] >= 20 && u[c] * 2 >= n[c]) printf "warning: %s: %d of %d samples have an unresolved frame (no frame pointers, or no symbols); stacks will be truncated: rebuild with -fno-omit-frame-pointer, install debug symbols, or capture with --call-graph dwarf\n", c, u[c], n[c] }' >&2
  say "frame pointer check done"
}

do_capture() {
  perf_works || { echo "perf is not usable; run this script without --capture first" >&2; exit 1; }
  if [[ $(paranoid) != "?" && $(paranoid) -gt 0 && $EUID -ne 0 ]]; then
    echo "kernel.perf_event_paranoid is $(paranoid): run with sudo to profile the whole system" >&2
    exit 1
  fi
  umask 077  # a whole-system trace names every process on the host: owner only, then handed to the sudo user below
  mkdir -p "$outdir"
  local stamp start stop text manifest token
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  tmp=$(mktemp -d)
  start=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  say "recording for $capture s ..."
  perf record -q -F 99 -a -g -o "$tmp/perf.data" -- sleep "$capture" >/dev/null 2>"$tmp/err" || {
    echo "perf record failed: $(tr '\n' ' ' <"$tmp/err" | cut -c1-300)" >&2
    exit 1
  }
  stop=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  text=$outdir/perf-script-$stamp.txt
  manifest=$outdir/manifest-$stamp.json
  # Replaced on the host: home directories (user names) and the host name, the two things in the text that name people.
  perf script -f -i "$tmp/perf.data" --header 2>/dev/null |
    sed -E 's#(/home/)[^/ ;:)]+#\1<user>#g; s#(/Users/)[^/ ;:)]+#\1<user>#g; s#^(\# hostname : ).*#\1<host>#' >"$text"
  token=$(tr -d '-' </proc/sys/kernel/random/uuid | cut -c1-16)
  cat >"$manifest" <<EOF
{
  "schemaVersion": 1,
  "traceType": "perf_script_text",
  "trigger": {"name": "manual", "type": "manual"},
  "window": {"startUtc": "$start", "stopUtc": "$stop"},
  "tool": {"name": "perf", "version": "$(perf --version 2>&1 | awk '{print $NF}')", "command": "perf record -F 99 -a -g -- sleep $capture"},
  "os": {"family": "linux", "build": "$(uname -r)"},
  "hostToken": "$token",
  "agentVersion": "configure-tracing.sh/1"
}
EOF
  if [[ -n ${SUDO_UID:-} ]]; then chown "$SUDO_UID:${SUDO_GID:-$SUDO_UID}" "$text" "$manifest" 2>/dev/null || true; fi
  say "wrote $text ($(wc -c <"$text") bytes) and $manifest"
  say "review them, then upload with an API key:"
  say "  curl -sS -X POST https://api.performanceinsights.ai/api/v1/traces -H \"Authorization: Bearer \$DUMPTRIAGE_API_KEY\" \\"
  say "       -F manifest='<$manifest' -F file=@$text"
}

if ((undo)); then
  need_root
  run rm -f "$sysctl_conf"
  say "removed $sysctl_conf; the live sysctl values stay until the next boot"
  exit 0
fi

if [[ -n $capture ]]; then
  do_capture
  exit 0
fi

if ((check)); then
  rc=0
  report_perf || rc=1
  report_privileges
  report_tracefs
  ((fp)) && frame_pointer_check
  exit $rc
fi

need_root
if perf_works; then say "perf is already installed: $(perf --version 2>&1 | head -1)"; else install_perf; fi
if [[ $agent_user != root ]]; then
  if ((dry)); then
    echo "+ write $sysctl_conf: kernel.perf_event_paranoid = 0, kernel.kptr_restrict = 0"
  else
    mkdir -p "$sysctl_d"
    printf '%s\n' "# DumpTriage tracing: an unprivileged agent ($agent_user) needs system-wide perf and kernel symbols (configure-tracing.sh)" \
      "kernel.perf_event_paranoid = 0" "kernel.kptr_restrict = 0" >"$sysctl_conf"
  fi
  run sysctl -qw kernel.perf_event_paranoid=0 kernel.kptr_restrict=0
  warn "agent user $agent_user: perf_event_paranoid and kptr_restrict are now 0 for every local user; run the agent as root to avoid that"
fi
if ((dry)); then
  say "(dry run: perf would be installed by the command above)"
else
  report_perf || { echo "perf is still not usable after the install; check the package manager output above" >&2; exit 1; }
fi
report_privileges
report_tracefs
((fp)) && frame_pointer_check
say "next: enable the tracing section in the agent's config.json (see linux/dumptriage-agent/README.md)"

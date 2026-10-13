#!/usr/bin/env bash
# Configures Linux crash-dump capture, the counterpart of windows/Set-WerLocalDumps.ps1.
#
#   sudo linux/configure-dumps.sh [--dir DIR] [--retain-days N] [--kdump] [--dry-run]
#   sudo linux/configure-dumps.sh --undo [--dry-run]
#
# Sets up, persistently (survives reboot) and idempotently:
#   - kernel.core_pattern to write raw, uncompressed ELF cores to DIR (default /var/crash/dumptriage), so
#     the DumpTriage agent can upload them as they are. DIR is mode 1777 like /tmp; each core stays 0600
#     and owned by the user whose process crashed. Replaces the pipe to apport / systemd-coredump.
#   - unlimited core size for login sessions (limits.d) and for services (systemd DefaultLimitCORE)
#   - retention: cores older than --retain-days (default 14) are removed by systemd-tmpfiles
#   - with --kdump: kernel crash dumps (kdump-tools on Debian/Ubuntu, kexec-tools on RHEL family).
#     This reserves memory with the crashkernel= boot parameter, so it needs a reboot to take effect.
# --undo restores the previous core_pattern and apport setting and removes the files written here
# (captured cores in DIR are left alone). Needs root.
set -euo pipefail

dir=/var/crash/dumptriage retain=14 kdump=0 undo=0 dry=0
while (($#)); do
  case $1 in
    --dir) dir=${2:?}; shift 2 ;;
    --retain-days) retain=${2:?}; shift 2 ;;
    --kdump) kdump=1; shift ;;
    --undo) undo=1; shift ;;
    --dry-run) dry=1; shift ;;
    -h | --help) sed -n '2,/^set -e/p' "$0" | sed '$d;s/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[[ $dir == /* ]] || { echo "--dir must be an absolute path" >&2; exit 2; }
[[ $retain =~ ^[0-9]+$ && $retain -ge 1 ]] || { echo "--retain-days must be a positive integer" >&2; exit 2; }
((EUID == 0 || dry)) || { echo "run as root (sudo)" >&2; exit 1; }

state=/etc/dumptriage-capture  # kept apart from the agent's /etc/dumptriage so uninstalling the agent cannot lose it
sysctl_conf=/etc/sysctl.d/60-dumptriage-core.conf
limits_conf=/etc/security/limits.d/60-dumptriage.conf
sd_system=/etc/systemd/system.conf.d/60-dumptriage.conf
sd_user=/etc/systemd/user.conf.d/60-dumptriage.conf
tmpfiles=/etc/tmpfiles.d/dumptriage.conf
apport=/etc/default/apport

run() { if ((dry)); then echo "+ $*"; else "$@"; fi; }
write() { # write FILE <<< content
  local f=$1; if ((dry)); then echo "+ write $f:"; sed 's/^/    /'; else mkdir -p "$(dirname "$f")"; cat >"$f"; fi
}
reexec() { if command -v systemctl >/dev/null && [[ -d /run/systemd/system ]]; then run systemctl daemon-reexec; fi; }

if ((undo)); then
  orig=$state/core_pattern.orig
  run rm -f "$sysctl_conf" "$limits_conf" "$sd_system" "$sd_user" "$tmpfiles"
  if [[ -f $orig ]]; then
    run sysctl -qw "kernel.core_pattern=$(cat "$orig")"
    run rm -f "$orig"
  fi
  if [[ -f $state/apport.orig ]]; then
    run cp "$state/apport.orig" "$apport"
    run rm -f "$state/apport.orig"
  fi
  reexec
  echo "Undone. Captured cores in $dir were left in place."
  exit 0
fi

run mkdir -p "$dir" "$state"
run chmod 1777 "$dir"
run chmod g-s "$dir"  # /var/crash is setgid on some distros; the sticky bit is what matters here

# Remember what was there, once, so --undo can put it back.
if [[ ! -f $state/core_pattern.orig ]]; then
  if ((dry)); then echo "+ save current core_pattern to $state/core_pattern.orig"; else cat /proc/sys/kernel/core_pattern >"$state/core_pattern.orig"; fi
fi
# apport rewrites core_pattern at boot unless disabled.
if [[ -f $apport ]] && grep -Eq '^enabled=1' "$apport"; then
  [[ -f $state/apport.orig ]] || run cp "$apport" "$state/apport.orig"
  run sed -i 's/^enabled=1/enabled=0/' "$apport"
fi

write "$sysctl_conf" <<CONF
# Written by DumpTriage linux/configure-dumps.sh
kernel.core_pattern = $dir/core.%e.%p.%t
CONF
if ((dry)); then echo "+ sysctl -p $sysctl_conf"; else sysctl -q -p "$sysctl_conf"; fi

write "$limits_conf" <<'CONF'
# Written by DumpTriage linux/configure-dumps.sh
*    soft core unlimited
root soft core unlimited
CONF
write "$sd_system" <<'CONF'
# Written by DumpTriage linux/configure-dumps.sh
[Manager]
DefaultLimitCORE=infinity
CONF
write "$sd_user" <<'CONF'
# Written by DumpTriage linux/configure-dumps.sh
[Manager]
DefaultLimitCORE=infinity
CONF
reexec

write "$tmpfiles" <<CONF
# Written by DumpTriage linux/configure-dumps.sh: keep $dir, drop cores older than $retain days
d $dir 1777 root root ${retain}d
CONF

if ((kdump)); then
  if command -v apt-get >/dev/null; then
    echo "kdump-tools kdump-tools/use_kdump boolean true" | { if ((dry)); then cat; else debconf-set-selections; fi; }
    run env DEBIAN_FRONTEND=noninteractive apt-get install -y linux-crashdump
  elif command -v dnf >/dev/null; then
    run dnf install -y kexec-tools
    run kdumpctl reset-crashkernel --kernel=ALL || true
    run systemctl enable kdump
  else
    echo "--kdump: unsupported distribution (need apt or dnf)" >&2
    exit 1
  fi
  echo "kdump packages installed. REBOOT required: the crashkernel= memory reservation only exists after a restart."
  echo "Kernel dumps then appear under /var/crash/<timestamp>/ (watched by the agent's default config)."
fi

((dry)) || echo "core_pattern now: $(cat /proc/sys/kernel/core_pattern)"

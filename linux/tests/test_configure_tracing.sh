#!/bin/bash
# Tests for ../configure-tracing.sh (website#1999). Plain bash like macos/tests/test_collect.sh: a fake perf, uname,
# apt-get and sysctl go first on PATH and log what they were asked; DT_* point the script at a fake /proc/sys,
# os-release and sysctl.d. Run: bash linux/tests/test_configure_tracing.sh
set -u

HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT="$HERE/../configure-tracing.sh"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/ct-test.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
PASSED=0 FAILED=0

fail() { printf 'FAIL %s: %s\n' "$CURRENT" "$1"; FAILED=$((FAILED + 1)); CURRENT_FAILED=1; }
assert_contains() { case "$1" in *"$2"*) ;; *) fail "expected '$2' in:
$1" ;; esac; }
assert_not_contains() { case "$1" in *"$2"*) fail "did not expect '$2' in:
$1" ;; esac; }
assert_eq() { [ "$1" = "$2" ] || fail "expected '$2', got '$1'"; }
done_test() { if [ "${CURRENT_FAILED:-0}" -eq 0 ]; then printf 'ok   %s\n' "$CURRENT"; PASSED=$((PASSED + 1)); fi; }

setup() { # name [perf-state: ok|missing|wrapper]
  CURRENT=$1 CURRENT_FAILED=0
  T="$WORK/$1" LOG="$T/calls.log"
  mkdir -p "$T/bin" "$T/sys/kernel" "$T/sysctl.d" "$T/out"
  : >"$LOG"
  echo 4 >"$T/sys/kernel/perf_event_paranoid"
  echo 1 >"$T/sys/kernel/kptr_restrict"
  printf 'ID=ubuntu\nID_LIKE=debian\n' >"$T/os-release"
  case ${2:-ok} in
    ok) fake_perf ;;
    wrapper) printf '#!/bin/sh\necho "WARNING: perf not found for kernel 6.8.0-1" >&2\nexit 2\n' >"$T/bin/perf"; chmod +x "$T/bin/perf" ;;
    missing) printf '#!/bin/sh\nexit 127\n' >"$T/bin/perf"; chmod +x "$T/bin/perf" ;;  # the host's own perf must not leak in
  esac
  for tool in apt-get dnf sysctl; do
    printf '#!/bin/sh\necho "%s $*" >>"%s"\n' "$tool" "$LOG" >"$T/bin/$tool"
    chmod +x "$T/bin/$tool"
  done
  printf '#!/bin/sh\n[ "$1" = "-r" ] && echo 6.8.0-147-generic || /usr/bin/uname "$@"\n' >"$T/bin/uname"; chmod +x "$T/bin/uname"
}

fake_perf() {
  cat >"$T/bin/perf" <<PERF
#!/bin/sh
echo "perf \$*" >>"$LOG"
case "\$1" in
  --version) echo "perf version 6.8.12" ;;
  record) out=; while [ \$# -gt 0 ]; do [ "\$1" = "-o" ] && out=\$2; shift; done; echo data >"\$out" ;;
  script) cat <<'TXT'
# ========
# hostname : payroll-db-01
# perf version : 6.8.12
# ========

job 1 [000] 1.000000: 1 cycles:P:
	ffffffffad9a52ca put_ctx+0x1a ([kernel.kallsyms])
	    7f0000 load+0x4 (/home/alice/projects/acme/libcfg.so)

TXT
  ;;
esac
PERF
  chmod +x "$T/bin/perf"
}

run() { # args...: runs the script with the fakes first on PATH; sets OUT and CODE
  OUT=$(PATH="$T/bin:$PATH" DT_OS_RELEASE="$T/os-release" DT_SYSCTL_D="$T/sysctl.d" DT_PROC_SYS="$T/sys" DT_TRACEFS="$T/none" bash "$SCRIPT" "$@" 2>&1)
  CODE=$?
}

setup check_reports_what_is_ready
run --check
assert_eq "$CODE" 0
assert_contains "$OUT" "perf: perf version 6.8.12"
assert_contains "$OUT" "kernel.perf_event_paranoid: 4; kernel.kptr_restrict: 1"
assert_contains "$OUT" "tracefs: not readable here"
assert_eq "$(cat "$LOG" | grep -c 'apt-get')" 0
done_test

setup check_fails_without_a_working_perf missing
run --check
assert_eq "$CODE" 1
assert_contains "$OUT" "perf: not usable"
done_test

setup a_perf_wrapper_for_another_kernel_counts_as_missing wrapper
run --check
assert_eq "$CODE" 1
assert_contains "$OUT" "perf: not usable"
done_test

setup install_on_ubuntu_uses_apt_not_snap missing
run --dry-run
assert_eq "$CODE" 0  # a dry run installs nothing and says so; it does not call the missing perf a failure
assert_contains "$OUT" "+ apt-get install -y linux-tools-common linux-tools-6.8.0-147-generic"
assert_not_contains "$OUT" "snap"
assert_contains "$OUT" "dry run: perf would be installed"
assert_not_contains "$OUT" "perf is still not usable"
done_test

setup install_on_debian_and_rhel_pick_their_package missing
printf 'ID=debian\n' >"$T/os-release"; run --dry-run
assert_contains "$OUT" "+ apt-get install -y linux-perf"
printf 'ID=rocky\nID_LIKE="rhel centos fedora"\n' >"$T/os-release"; run --dry-run
assert_contains "$OUT" "+ dnf install -y perf"
printf 'ID=plan9\n' >"$T/os-release"; run --dry-run
assert_contains "$OUT" "unknown distribution"
done_test

setup root_agent_changes_no_setting
run --dry-run
assert_contains "$OUT" "perf is already installed: perf version 6.8.12"
assert_contains "$OUT" "agent user: root, so system-wide perf works"
assert_not_contains "$OUT" "sysctl"
done_test

setup unprivileged_agent_gets_a_sysctl_file_and_a_warning
run --dry-run --agent-user dumptriage
assert_contains "$OUT" "+ write $T/sysctl.d/60-dumptriage-tracing.conf: kernel.perf_event_paranoid = 0, kernel.kptr_restrict = 0"
assert_contains "$OUT" "+ sysctl -qw kernel.perf_event_paranoid=0 kernel.kptr_restrict=0"
assert_contains "$OUT" "run the agent as root to avoid that"
[ ! -e "$T/sysctl.d/60-dumptriage-tracing.conf" ] || fail "a dry run wrote the file"
done_test

setup undo_removes_only_its_file
echo keep >"$T/sysctl.d/other.conf"; echo x >"$T/sysctl.d/60-dumptriage-tracing.conf"
run --undo --dry-run
assert_contains "$OUT" "+ rm -f $T/sysctl.d/60-dumptriage-tracing.conf"
[ -e "$T/sysctl.d/60-dumptriage-tracing.conf" ] || fail "a dry run removed the file"
done_test

setup capture_writes_redacted_text_a_manifest_and_the_upload_command
echo 0 >"$T/sys/kernel/perf_event_paranoid"
run --capture 5 --output-dir "$T/out"
assert_eq "$CODE" 0
text=$(ls "$T/out"/perf-script-*.txt); manifest=$(ls "$T/out"/manifest-*.json)
assert_contains "$(cat "$text")" "# hostname : <host>"
assert_contains "$(cat "$text")" "/home/<user>/projects/acme/libcfg.so"
assert_not_contains "$(cat "$text")" "payroll-db-01"
assert_not_contains "$(cat "$text")" "alice"
assert_contains "$(cat "$manifest")" '"traceType": "perf_script_text"'
assert_contains "$(cat "$manifest")" '"trigger": {"name": "manual", "type": "manual"}'
assert_contains "$(cat "$manifest")" '"build": "6.8.0-147-generic"'
assert_contains "$(cat "$manifest")" '"version": "6.8.12"'
assert_contains "$OUT" "/api/v1/traces"
assert_contains "$OUT" "-F manifest='<$manifest' -F file=@$text"
assert_contains "$(cat "$LOG")" "perf record -q -F 99 -a -g -o"
if command -v python3 >/dev/null && python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$manifest" 2>/dev/null; then :; else fail "manifest is not JSON"; fi
done_test

setup a_failed_capture_leaves_no_scratch_directory_and_the_files_are_owner_only
echo 0 >"$T/sys/kernel/perf_event_paranoid"
printf '#!/bin/sh\ncase "$1" in --version) echo "perf version 6.8.12" ;; record) echo "no permission" >&2; exit 1 ;; esac\n' >"$T/bin/perf"
mkdir -p "$T/tmp"
OUT=$(TMPDIR="$T/tmp" PATH="$T/bin:$PATH" DT_PROC_SYS="$T/sys" bash "$SCRIPT" --capture 5 --output-dir "$T/out" 2>&1); CODE=$?
assert_eq "$CODE" 1
assert_contains "$OUT" "perf record failed: no permission"
assert_eq "$(ls "$T/tmp" | wc -l | tr -d ' ')" 0
done_test

setup capture_files_are_owner_only
echo 0 >"$T/sys/kernel/perf_event_paranoid"
run --capture 5 --output-dir "$T/out"
for f in "$T"/out/*; do assert_eq "$(stat -c %a "$f")" 600; done
done_test

setup capture_needs_root_when_perf_is_locked_down
run --capture 5 --output-dir "$T/out"
if [ "$(id -u)" -ne 0 ]; then
  assert_eq "$CODE" 1
  assert_contains "$OUT" "kernel.perf_event_paranoid is 4: run with sudo"
fi
done_test

setup bad_options_are_usage_errors
run --capture soon; assert_eq "$CODE" 2
run --capture 601; assert_eq "$CODE" 2
run --agent-user "Not A User"; assert_eq "$CODE" 2
run --frobnicate; assert_eq "$CODE" 2
done_test

setup frame_pointer_check_warns_about_unresolved_stacks
cat >"$T/bin/perf" <<PERF
#!/bin/sh
case "\$1" in
  --version) echo "perf version 6.8.12" ;;
  record) out=; while [ \$# -gt 0 ]; do [ "\$1" = "-o" ] && out=\$2; shift; done; echo data >"\$out" ;;
  script) i=0; while [ \$i -lt 30 ]; do printf 'python3\n\t7f00 [unknown]\n\t7f01 PyEval\n\n'; printf 'nginx\n\t7f02 ngx_http\n\t7f03 main\n\n'; i=\$((i+1)); done ;;
esac
PERF
chmod +x "$T/bin/perf"
run --check --check-frame-pointers
assert_contains "$OUT" "warning: python3: 30 of 30 samples have an unresolved frame"
assert_not_contains "$OUT" "warning: nginx"
assert_contains "$OUT" "frame pointer check done"
done_test

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]

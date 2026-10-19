#!/bin/bash
# Tests for ../dumptriage-collect.sh (website#1746). Plain bash, no bats: runs on Linux
# CI and on a Mac. Report fixtures are the backend's real ones; curl, security,
# sudo and spindump are fakes put first on PATH, which log what they were asked.
#
# Run: bash macos/tests/test_collect.sh

set -u

HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT="$HERE/../dumptriage-collect.sh"
FIXTURES="$HERE/fixtures"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/collect-test.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

PASSED=0
FAILED=0

fail() {
    printf 'FAIL %s: %s\n' "$CURRENT" "$1"
    FAILED=$((FAILED + 1))
    CURRENT_FAILED=1
}

assert_contains() {  # haystack needle
    case "$1" in
        *"$2"*) ;;
        *) fail "expected to find '$2' in:
$1" ;;
    esac
}

assert_not_contains() {
    case "$1" in
        *"$2"*) fail "did not expect '$2' in:
$1" ;;
    esac
}

assert_eq() {
    [ "$1" = "$2" ] || fail "expected '$2', got '$1'"
}

# A fresh report folder and fake tools for each test.
setup() {
    CURRENT=$1
    CURRENT_FAILED=0
    T="$WORK/$1"
    mkdir -p "$T/reports" "$T/bin"
    REPORTS="$T/reports"
    LOG="$T/calls.log"
    : > "$LOG"
    cp "$FIXTURES/crash-constructed-monterey-null-call.ips" "$REPORTS/napper-2026-10-15-101010.ips"
    cp "$FIXTURES/panic-tahoe-intel-iclgraphics.panic" "$REPORTS/Kernel-2026-10-14-090909.panic"
    cp "$FIXTURES/spindump-catalina-slack.txt" "$REPORTS/xterm_2026-10-13-080808.hang"
    printf '{"bug_type":"298","os_version":"macOS 14.5"}\n{}\n' > "$REPORTS/JetsamEvent-2026-10-15.ips"
    printf 'not a report\n' > "$REPORTS/something.diag"
    cp "$FIXTURES/crash-constructed-monterey-null-call.ips" "$REPORTS/old-2020-01-01-000000.ips"
    touch -t 202001010000 "$REPORTS/old-2020-01-01-000000.ips"
    # Newest first: napper, Kernel, xterm. Future times are "recent" too, and need no date arithmetic.
    touch -t 203003010000 "$REPORTS/napper-2026-10-15-101010.ips"
    touch -t 203002010000 "$REPORTS/Kernel-2026-10-14-090909.panic"
    touch -t 203001010000 "$REPORTS/xterm_2026-10-13-080808.hang"

    cat > "$T/bin/curl" <<EOF
#!/bin/bash
# fake curl: logs its arguments and the config it reads on stdin, answers \$FAKE_STATUS
{ printf 'curl'; for a in "\$@"; do printf ' [%s]' "\$a"; done; printf '\n'; sed 's/^/config: /'; } >> "$LOG"
status=\${FAKE_STATUS:-201}
if [ "\$status" = 201 ]; then
    printf '{"id":"11111111-2222-3333-4444-555555555555","dump_type":"macos_crash","status":"uploaded"}\n%s' "\$status"
else
    printf '{"detail":"macOS crash reports, kernel panics and spindumps are not supported on this service yet"}\n%s' "\$status"
fi
EOF
    cat > "$T/bin/security" <<EOF
#!/bin/bash
printf 'security %s\n' "\$*" >> "$LOG"
[ -n "\${FAKE_KEYCHAIN:-}" ] || exit 44
printf '%s\n' "\$FAKE_KEYCHAIN"
EOF
    cat > "$T/bin/sudo" <<EOF
#!/bin/bash
printf 'sudo %s\n' "\$*" >> "$LOG"
"\$@"
EOF
    cat > "$T/bin/spindump" <<EOF
#!/bin/bash
# fake spindump TARGET SECONDS -file OUT
printf 'spindump %s\n' "\$*" >> "$LOG"
cp "$FIXTURES/spindump-catalina-slack.txt" "\$4"
EOF
    chmod +x "$T/bin/"*
}

run() {  # stdin, then the script's arguments
    input=$1
    shift
    OUT=$(printf '%s' "$input" | PATH="$T/bin:$PATH" TMPDIR="$T" "$SCRIPT" "$@" 2>&1)
    CODE=$?
}

done_test() {
    [ "$CURRENT_FAILED" -eq 0 ] && PASSED=$((PASSED + 1)) && printf 'ok   %s\n' "$CURRENT"
}

export DUMPTRIAGE_API_URL=https://triage.example.com
export DUMPTRIAGE_API_KEY=dt_test_key_123
unset DUMPTRIAGE_PORTAL_URL FAKE_STATUS FAKE_KEYCHAIN

setup lists_recent_reports_by_kind_newest_first
run "" --dir "$REPORTS" --dry-run --all
assert_eq "$CODE" 0
assert_contains "$OUT" " 1) crash  $REPORTS/napper-2026-10-15-101010.ips"
assert_contains "$OUT" "panic  $REPORTS/Kernel-2026-10-14-090909.panic"
assert_contains "$OUT" "hang   $REPORTS/xterm_2026-10-13-080808.hang"
assert_not_contains "$OUT" "JetsamEvent"   # bug_type 298 is not triaged
assert_not_contains "$OUT" "something.diag"
assert_not_contains "$OUT" "old-2020"      # older than --days 7
assert_contains "$OUT" "Dry run: nothing sent."
assert_eq "$(cat "$LOG")" ""
done_test

setup days_widens_the_window
run "" --dir "$REPORTS" --dry-run --all --days 100000
assert_contains "$OUT" "old-2020-01-01-000000.ips"
done_test

setup picks_by_number_confirms_and_uploads_with_bearer_auth_from_stdin_config
run "2
y
" --dir "$REPORTS"
assert_eq "$CODE" 0
assert_contains "$OUT" "These files will be sent:"
assert_contains "$OUT" "panic  $REPORTS/Kernel-2026-10-14-090909.panic"
assert_contains "$OUT" "sent $REPORTS/Kernel-2026-10-14-090909.panic"
assert_contains "$OUT" "report: https://triage.example.com/dumps/11111111-2222-3333-4444-555555555555"
calls=$(cat "$LOG")
assert_contains "$calls" "[-F] [file=@$REPORTS/Kernel-2026-10-14-090909.panic]"
assert_contains "$calls" "[-H] [Expect:]"  # no 100-continue: the API answers it 417 (website#1801)
assert_contains "$calls" "[https://triage.example.com/api/v1/dumps]"
assert_contains "$calls" 'config: header = "Authorization: Bearer dt_test_key_123"'
assert_not_contains "$(grep '^curl' "$LOG")" "dt_test_key_123"  # never on the command line
assert_eq "$(grep -c '^curl' "$LOG")" 1
done_test

setup answering_no_sends_nothing
run "1 3
n
" --dir "$REPORTS"
assert_eq "$CODE" 0
assert_contains "$OUT" "Not sent."
assert_eq "$(grep -c '^curl' "$LOG")" 0
done_test

setup an_empty_choice_sends_nothing
run "
" --dir "$REPORTS"
assert_contains "$OUT" "Nothing selected; nothing sent."
assert_eq "$(grep -c '^curl' "$LOG")" 0
done_test

setup a_number_outside_the_list_is_refused
run "9
" --dir "$REPORTS"
assert_eq "$CODE" 2
assert_contains "$OUT" "9 is not in the list (1-3)"
done_test

setup all_uploads_every_listed_report_and_the_portal_link_can_differ
DUMPTRIAGE_PORTAL_URL=https://portal.example.com run "y
" --dir "$REPORTS" --all
assert_eq "$CODE" 0
assert_eq "$(grep -c '^curl' "$LOG")" 3
assert_contains "$OUT" "report: https://portal.example.com/dumps/11111111-2222-3333-4444-555555555555"
done_test

setup the_key_comes_from_the_keychain_when_the_environment_has_none
DUMPTRIAGE_API_KEY="" FAKE_KEYCHAIN=dt_from_keychain run "1
y
" --dir "$REPORTS"
assert_eq "$CODE" 0
assert_contains "$(cat "$LOG")" "security find-generic-password -s dumptriage -w"
assert_contains "$(cat "$LOG")" 'config: header = "Authorization: Bearer dt_from_keychain"'
done_test

setup no_key_anywhere_stops_before_sending
DUMPTRIAGE_API_KEY="" run "1
y
" --dir "$REPORTS"
assert_eq "$CODE" 2
assert_contains "$OUT" "no API key"
assert_eq "$(grep -c '^curl' "$LOG")" 0
done_test

setup a_refused_upload_says_why_and_fails
FAKE_STATUS=422 run "1
y
" --dir "$REPORTS"
assert_eq "$CODE" 1
assert_contains "$OUT" "NOT sent $REPORTS/napper-2026-10-15-101010.ips: HTTP 422 macOS crash reports, kernel panics and spindumps are not supported"
done_test

setup spindump_samples_the_target_with_sudo_and_sends_it
run "y
" --dir "$REPORTS" --spindump Xcode
assert_eq "$CODE" 0
calls=$(cat "$LOG")
assert_contains "$calls" "sudo spindump Xcode 10 -file $T/dumptriage-spindump-"
assert_eq "$(grep -c '^curl' "$LOG")" 1
assert_contains "$calls" "[file=@$T/dumptriage-spindump-"
assert_not_contains "$OUT" "Which to send?"  # only the fresh spindump, no picker
[ -z "$(ls "$T"/dumptriage-spindump-* 2>/dev/null)" ] || fail "the temporary spindump was not removed"
done_test

setup a_declined_spindump_is_removed_too
run "n
" --dir "$REPORTS" --spindump Xcode
assert_eq "$CODE" 0
assert_contains "$OUT" "Not sent."
assert_eq "$(grep -c '^curl' "$LOG")" 0
[ -z "$(ls "$T"/dumptriage-spindump-* 2>/dev/null)" ] || fail "a declined spindump was left in TMPDIR"
done_test

setup a_spindump_without_a_key_is_removed_too
DUMPTRIAGE_API_URL= run "" --dir "$REPORTS" --spindump Xcode
assert_eq "$CODE" 2
[ -z "$(ls "$T"/dumptriage-spindump-* 2>/dev/null)" ] || fail "a spindump that could not be sent was left in TMPDIR"
done_test

setup dry_run_spindump_runs_nothing
run "" --dir "$REPORTS" --spindump 4242 --dry-run
assert_eq "$CODE" 0
assert_contains "$OUT" "Would take a spindump of 4242"
assert_eq "$(cat "$LOG")" ""
done_test

setup an_empty_folder_says_so
mkdir -p "$T/empty"
run "" --dir "$T/empty"
assert_eq "$CODE" 0
assert_contains "$OUT" "No crash, panic or hang reports from the last 7 day(s)"
done_test

setup bad_options_are_usage_errors
run "" --days soon
assert_eq "$CODE" 2
run "" --frobnicate
assert_eq "$CODE" 2
done_test

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]

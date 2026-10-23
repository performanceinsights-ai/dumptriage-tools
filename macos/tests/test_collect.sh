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
# fake curl: logs its arguments and the config it reads on stdin, answers \$FAKE_STATUS;
# GET /api/v1/config answers \$FAKE_CONFIG, or fails as an unreachable service would
for a in "\$@"; do last=\$a; done
case "\$last" in
    */api/v1/config)
        printf 'curl config %s\n' "\$last" >> "$LOG"
        [ -n "\${FAKE_CONFIG:-}" ] || exit 7
        printf '%s\n' "\$FAKE_CONFIG"
        exit 0 ;;
esac
{ printf 'curl'; for a in "\$@"; do printf ' [%s]' "\$a"; done; printf '\n'; sed 's/^/config: /'; } >> "$LOG"
status=\${FAKE_STATUS:-201}
if [ "\$status" = 401 ]; then
    printf '{"detail":"invalid API key"}\n%s' "\$status"
elif [ "\$status" = 201 ]; then
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
# fake spindump ... -file OUT
printf 'spindump %s\n' "\$*" >> "$LOG"
while [ \$# -gt 0 ]; do [ "\$1" = -file ] && out=\$2; shift; done
cp "$FIXTURES/spindump-catalina-slack.txt" "\$out"
EOF
    cat > "$T/bin/sample" <<EOF
#!/bin/bash
# fake sample TARGET SECONDS INTERVAL -file OUT
printf 'sample %s\n' "\$*" >> "$LOG"
while [ \$# -gt 0 ]; do [ "\$1" = -file ] && out=\$2; shift; done
cp "$FIXTURES/sample-macos26.txt" "\$out"
EOF
    cat > "$T/bin/sw_vers" <<EOF
#!/bin/bash
case "\$1" in -buildVersion) echo 25G83 ;; -productVersion) echo 26.6.2 ;; esac
EOF
    # fake xcrun: --find xctrace (fails when \$FAKE_NO_XCODE is set), xctrace version, record (makes the .trace folder,
    # fails unprivileged when \$FAKE_RECORD_NEEDS_SUDO is set), export (a real export, \$FAKE_EXPORT, as the XML)
    cat > "$T/bin/xcrun" <<EOF
#!/bin/bash
printf 'xcrun %s\n' "\$*" >> "$LOG"
if [ "\$1" = --find ]; then [ -z "\${FAKE_NO_XCODE:-}" ] && echo /Applications/Xcode.app/Contents/Developer/usr/bin/xctrace; exit \$([ -z "\${FAKE_NO_XCODE:-}" ] && echo 0 || echo 1); fi
[ "\$1" = xctrace ] || exit 1
shift
case "\$1" in
    version) echo "xctrace version 16.0 (17F113)" ;;
    record)
        if [ -n "\${FAKE_RECORD_NEEDS_SUDO:-}" ] && [ -z "\${SUDO_FAKE:-}" ]; then echo "not permitted" >&2; exit 1; fi
        while [ \$# -gt 0 ]; do [ "\$1" = --output ] && out=\$2; shift; done
        mkdir -p "\$out" && echo bundle > "\$out/data" ;;
    export)
        while [ \$# -gt 0 ]; do [ "\$1" = --output ] && out=\$2; shift; done
        cp "\${FAKE_EXPORT:-$FIXTURES/xctrace-time-profile-macos26.xml}" "\$out" ;;
esac
EOF
    # sudo marks its children so the fake xcrun can tell a privileged run
    sed -i.bak 's/^"\$@"$/SUDO_FAKE=1 "\$@"/' "$T/bin/sudo" && rm -f "$T/bin/sudo.bak"
    chmod +x "$T/bin/"*
}

run() {  # stdin, then the script's arguments
    input=$1
    shift
    OUT=$(printf '%s' "$input" | PATH="$T/bin:$PATH" TMPDIR="$T" HOME="$T/home" "$SCRIPT" "$@" 2>&1)
    CODE=$?
}

done_test() {
    [ "$CURRENT_FAILED" -eq 0 ] && PASSED=$((PASSED + 1)) && printf 'ok   %s\n' "$CURRENT"
}

export DUMPTRIAGE_API_URL=https://triage.example.com
export DUMPTRIAGE_API_KEY=dt_test_key_123
unset DUMPTRIAGE_PORTAL_URL FAKE_STATUS FAKE_KEYCHAIN FAKE_CONFIG

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

setup no_key_anywhere_says_to_sign_up_before_listing_reports
DUMPTRIAGE_API_KEY="" FAKE_CONFIG='{"self_hosted": false, "signup_enabled": true, "llm_providers": []}' \
    DUMPTRIAGE_PORTAL_URL=https://portal.example.com/ run "1
y
" --dir "$REPORTS"
assert_eq "$CODE" 2
assert_contains "$OUT" "no API key"
assert_contains "$OUT" "1. Sign up at https://portal.example.com/signup"
assert_contains "$OUT" "2. Create an upload-only API key on https://portal.example.com/account"
assert_contains "$OUT" 'security add-generic-password -U -s dumptriage -a "$USER" -w'
assert_contains "$OUT" "4. Run this again."
assert_contains "$OUT" "Note: to sign up yourself, open https://portal.example.com/signup in a browser."
assert_not_contains "$OUT" "Which to send?"  # told before picking anything
assert_contains "$(cat "$LOG")" "curl config https://triage.example.com/api/v1/config"
assert_eq "$(grep -c '^curl \[' "$LOG")" 0
done_test

setup no_key_on_a_service_without_sign_up_says_to_ask_for_an_account
DUMPTRIAGE_API_KEY="" FAKE_CONFIG='{"self_hosted":true,"signup_enabled":false}' run "" --dir "$REPORTS"
assert_eq "$CODE" 2
assert_contains "$OUT" "1. Ask whoever runs https://triage.example.com for an account"
assert_not_contains "$OUT" "/signup"  # a service without sign-up has no such page
assert_not_contains "$OUT" "Note: to sign up"
done_test

setup no_key_and_an_unreachable_service_still_says_where_a_key_comes_from
DUMPTRIAGE_API_KEY="" run "" --dir "$REPORTS"
assert_eq "$CODE" 2
assert_contains "$OUT" "1. Sign up, or sign in if you have an account, at https://triage.example.com"
assert_contains "$OUT" "https://triage.example.com/account"
# Sign-up may still be open; name the page in case the customer wants to sign up by hand.
assert_contains "$OUT" "Note: to sign up yourself, open https://triage.example.com/signup in a browser."
done_test

setup no_key_stops_before_asking_for_sudo
DUMPTRIAGE_API_KEY="" run "" --dir "$REPORTS" --spindump Xcode
assert_eq "$CODE" 2
assert_contains "$OUT" "no API key"
assert_eq "$(grep -c '^sudo' "$LOG")" 0
done_test

setup a_refused_key_says_to_replace_it_and_stops_sending
DUMPTRIAGE_API_KEY="" FAKE_KEYCHAIN=dt_revoked FAKE_STATUS=401 run "y
" --dir "$REPORTS" --all
assert_eq "$CODE" 1
assert_contains "$OUT" "NOT sent $REPORTS/napper-2026-10-15-101010.ips"
assert_contains "$OUT" "refused the API key (HTTP 401)"
assert_contains "$OUT" "https://triage.example.com/account"
assert_contains "$OUT" 'security add-generic-password -U -s dumptriage -a "$USER" -w'
assert_eq "$(grep -c '^curl \[' "$LOG")" 1  # the other two would be refused the same way
done_test

setup a_refused_key_from_the_environment_says_to_set_it_there
FAKE_STATUS=401 run "1
y
" --dir "$REPORTS"
assert_eq "$CODE" 1
assert_contains "$OUT" "in DUMPTRIAGE_API_KEY."
assert_not_contains "$OUT" "security add-generic-password"
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

setup no_url_stops_before_taking_a_spindump
DUMPTRIAGE_API_URL= run "" --dir "$REPORTS" --spindump Xcode
assert_eq "$CODE" 2
assert_eq "$(grep -c '^sudo' "$LOG")" 0
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

# --- performance profiles (#31) -------------------------------------------------------------------------------

# The fold, pinned to real xctrace exports (macos/tests/fixtures/xctrace-time-profile-macos*.xml, first rows of the
# 2026-10-23 runner captures) and checked against an independent XML reader that resolves id/ref: Xcode 26 (macOS 26)
# wraps the stack in <tagged-backtrace>, Xcode 15 and 16 (macOS 14, 15) put <backtrace> in the row; macOS 14 left the
# unstripped burner's own frames as addresses.
fold() {
    ( eval "$(sed -n '/^fold_xctrace_export() {/,/^}/p' "$SCRIPT")"; fold_xctrace_export "$1" )
}

setup the_fold_reads_the_tagged_backtrace_of_xcode_26
FOLDED=$(fold "$FIXTURES/xctrace-time-profile-macos26.xml")
assert_contains "$FOLDED" "dtburn;start;main;dt_burn_outer;dt_burn_inner 202"
assert_eq "$(printf '%s\n' "$FOLDED" | wc -l | tr -d ' ')" 110
assert_eq "$(printf '%s\n' "$FOLDED" | awk '{s+=$NF} END{print s}')" 338
assert_not_contains "$FOLDED" "&amp;"   # entities are unescaped (a C++ reference is a real "&")
assert_not_contains "$FOLDED" "&lt;"
done_test

setup the_fold_reads_the_plain_backtrace_of_xcode_16
FOLDED=$(fold "$FIXTURES/xctrace-time-profile-macos15.xml")
assert_contains "$FOLDED" "dtburn;start;main;dt_burn_outer;dt_burn_inner 149"
assert_eq "$(printf '%s\n' "$FOLDED" | awk '{s+=$NF} END{print s}')" 350
done_test

setup the_fold_keeps_the_addresses_xcode_15_left_unnamed
FOLDED=$(fold "$FIXTURES/xctrace-time-profile-macos14.xml")
assert_contains "$FOLDED" "dtburn;start;0x100ed3e8b;0x100ed3eb7;0x100ed3ef4 14"
assert_eq "$(printf '%s\n' "$FOLDED" | awk '{s+=$NF} END{print s}')" 264
done_test

setup profile_seconds_are_checked_and_trace_options_stand_alone
run "" --profile 3
assert_eq "$CODE" 2
assert_contains "$OUT" "seconds must be 5 to 120, not 3"
run "" --profile 500 --dry-run
assert_eq "$CODE" 2
run "" --sample
assert_eq "$CODE" 2
run "" --spindump 123 --profile
assert_eq "$CODE" 2
assert_contains "$OUT" "run it apart from"
done_test

setup a_dry_run_profile_captures_and_sends_nothing
run "" --dry-run --profile 15 --sample 4242 20 --xctrace
assert_eq "$CODE" 0
assert_contains "$OUT" "Would sample every process for 20 s with spindump (sudo)"   # the last number given is the length
assert_contains "$OUT" "Would sample 4242 for 20 s with sample"
assert_contains "$OUT" "Would record every process for 20 s with xctrace"
assert_contains "$OUT" "Dry run: nothing captured, nothing sent."
assert_eq "$(cat "$LOG")" ""
done_test

setup a_profile_is_captured_with_sudo_confirmed_and_uploaded_as_a_trace_with_its_manifest
run "y
" --profile 12
assert_eq "$CODE" 0
calls=$(cat "$LOG")
assert_contains "$calls" "sudo spindump -notarget 12 10 -file "
assert_contains "$OUT" "spindump_text"
assert_contains "$OUT" "sent spindump_text (trace 11111111-2222-3333-4444-555555555555)"
assert_contains "$calls" "[https://triage.example.com/api/v1/traces]"
assert_contains "$calls" 'config: header = "Authorization: Bearer dt_test_key_123"'
assert_contains "$calls" '"traceType":"spindump_text"'
assert_contains "$calls" '"trigger":{"name":"manual","type":"manual"}'
assert_contains "$calls" '"tool":{"name":"spindump","version":"macOS 26.6.2","command":"spindump -notarget 12 10 -file <file>"}'
assert_contains "$calls" '"os":{"family":"macos","build":"25G83"}'
assert_contains "$calls" '"agentVersion":"0.1.0"'
assert_not_contains "$(grep -o 'manifest=[^]]*' "$LOG")" "$T"   # no folder of ours, so no user name, in the manifest
assert_eq "$(grep -c '^curl' "$LOG")" 1
# The work folder is gone, and the host token is made once and kept.
assert_eq "$(ls "$T" | grep -c '^dumptriage-trace')" 0
TOKEN=$(cat "$T/home/.config/dumptriage/host-token")
assert_eq "${#TOKEN}" 16
run "y
" --profile 12
assert_eq "$(cat "$T/home/.config/dumptriage/host-token")" "$TOKEN"
assert_contains "$(cat "$LOG")" "\"hostToken\":\"$TOKEN\""
done_test

setup answering_no_to_a_profile_sends_nothing_and_cleans_up
run "n
" --profile
assert_contains "$OUT" "Not sent."
assert_eq "$(grep -c '^curl' "$LOG")" 0
assert_eq "$(ls "$T" | grep -c '^dumptriage-trace')" 0
done_test

setup sample_takes_a_process_and_seconds
run "y
" --sample 4242 20
assert_eq "$CODE" 0
calls=$(cat "$LOG")
assert_contains "$calls" "sample 4242 20 1 -file "
assert_not_contains "$calls" "sudo"   # no privilege for a process of your own
assert_contains "$calls" '"traceType":"sample_text"'
assert_contains "$calls" '"command":"sample <process> 20 1 -file <file>"'  # the process is not named in the manifest
done_test

setup xctrace_folds_on_the_mac_and_only_the_stacks_leave
run "y
" --xctrace 9
assert_eq "$CODE" 0
calls=$(cat "$LOG")
assert_contains "$calls" "xcrun xctrace record --template Time Profiler --all-processes --time-limit 9s --no-prompt --output "
assert_not_contains "$calls" "sudo"   # unprivileged first, as a customer would run it
assert_contains "$calls" "--xpath /trace-toc/run[@number=\"1\"]/data/table[@schema=\"time-profile\"]"
assert_contains "$OUT" "xctrace_export"
assert_contains "$calls" '"traceType":"xctrace_export"'
assert_contains "$calls" '"tool":{"name":"xctrace","version":"xctrace version 16.0 (17F113)"'
uploaded=$(grep -o 'file=@[^]]*' "$LOG" | head -1)
assert_contains "$uploaded" "xctrace.folded"
assert_not_contains "$calls" "cap.trace]"   # the .trace bundle is never the upload
assert_eq "$(ls "$T" | grep -c '^dumptriage-trace')" 0
done_test

setup xctrace_retries_with_sudo_when_the_unprivileged_record_is_refused
FAKE_RECORD_NEEDS_SUDO=1 run "y
" --xctrace 9
assert_eq "$CODE" 0
assert_contains "$OUT" "trying again with sudo"
assert_contains "$(cat "$LOG")" "sudo xcrun xctrace record"
done_test

setup xctrace_needs_xcode
FAKE_NO_XCODE=1 run "" --xctrace
assert_eq "$CODE" 1
assert_contains "$OUT" "xctrace is part of Xcode; the Command Line Tools alone do not have it"
assert_eq "$(grep -c '^curl' "$LOG")" 0
done_test

setup a_service_without_traces_says_so_and_a_refused_key_stops
FAKE_STATUS=403 run "y
" --profile
assert_eq "$CODE" 1
assert_contains "$OUT" "NOT sent: HTTP 403"
FAKE_STATUS=401 run "y
" --profile
assert_eq "$CODE" 1
assert_contains "$OUT" "The service refused the API key (HTTP 401)"
done_test

# The shared tracing fixture (#26): this suite only checks it loads and is well formed; the
# trigger loop that uses it lands with the macOS capture (#31). jq on Linux CI, python3 on a Mac.
setup tracing_fixture_is_well_formed
TRIGGERS="$HERE/../../tests/fixtures/tracing/triggers.json"
if command -v jq > /dev/null 2>&1; then
    BAD=$(jq -r '[.cases[] | select((.expect.outcome | IN("fire","no_fire","cooldown","capped") | not)
        or ((.expect | has("atSec")) != (.expect.outcome != "no_fire")))] | length' "$TRIGGERS")
    OUTCOMES=$(jq -r '[.cases[].expect.outcome] | unique | join(",")' "$TRIGGERS")
elif command -v python3 > /dev/null 2>&1; then
    BAD=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(sum(1 for c in d["cases"] if c["expect"]["outcome"] not in ("fire","no_fire","cooldown","capped") or ("atSec" in c["expect"]) != (c["expect"]["outcome"] != "no_fire")))' "$TRIGGERS")
    OUTCOMES=$(python3 -c 'import json,sys; print(",".join(sorted({c["expect"]["outcome"] for c in json.load(open(sys.argv[1]))["cases"]})))' "$TRIGGERS")
else
    fail "neither jq nor python3 to read the fixture"
    BAD=0 OUTCOMES=capped,cooldown,fire,no_fire
fi
assert_eq "$BAD" 0
assert_eq "$OUTCOMES" "capped,cooldown,fire,no_fire"
done_test

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]

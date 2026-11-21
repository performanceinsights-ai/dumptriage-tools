#!/bin/bash
# Tests for install.sh's --reference (dumptriage-tools#77). install.sh runs for real as a normal user with --method cron,
# with HOME in a scratch folder and a fake `crontab` first on PATH, so nothing outside the folder changes.
# Run: bash linux/tests/test_install_reference.sh
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
INSTALL="$HERE/../dumptriage-agent/installer/install.sh"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/ref-test.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
PASSED=0 FAILED=0

fail() { printf 'FAIL %s: %s\n' "$CURRENT" "$1"; FAILED=$((FAILED + 1)); CURRENT_FAILED=1; }
assert_contains() { case "$1" in *"$2"*) ;; *) fail "expected '$2' in:
$1" ;; esac; }
assert_eq() { [ "$1" = "$2" ] || fail "expected '$2', got '$1'"; }
done_test() { if [ "${CURRENT_FAILED:-0}" -eq 0 ]; then printf 'ok   %s\n' "$CURRENT"; PASSED=$((PASSED + 1)); fi; }

if ((EUID == 0)); then echo "run as a normal user: as root install.sh writes /etc"; exit 1; fi
mkdir -p "$WORK/bin"
# `crontab -l` lists nothing (and must not read stdin, a terminal when run by hand); `crontab -` swallows the new table.
printf '#!/bin/sh\n[ "$1" = -l ] && exit 1\ncat >/dev/null\n' >"$WORK/bin/crontab"
chmod +x "$WORK/bin/crontab"

setup() { CURRENT=$1 CURRENT_FAILED=0; H="$WORK/$1"; mkdir -p "$H"; }
install_() { # args... : sets OUT and CODE
  OUT=$(env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$H" PATH="$WORK/bin:$PATH" \
    bash "$INSTALL" --method cron "$@" 2>&1)
  CODE=$?
}
reference() { python3 -c 'import json, sys; print(json.load(open(sys.argv[1])).get("reference"))' "$H/.config/dumptriage/config.json"; }

setup sets_the_label_trimmed
install_ --reference "  client=Acme;ticket=INC-4021 "
assert_eq "$CODE" 0
assert_eq "$(reference)" "client=Acme;ticket=INC-4021"
done_test

setup a_reinstall_without_the_option_keeps_it
install_ --reference "client=Acme"
install_ --policy AlwaysSubmit
assert_eq "$CODE" 0
assert_eq "$(reference)" "client=Acme"
done_test

setup an_empty_value_removes_it
install_ --reference "client=Acme"
install_ --reference ""
assert_eq "$CODE" 0
assert_eq "$(reference)" "None"
done_test

setup over_200_characters_is_refused_before_anything_is_installed
install_ --reference "$(printf 'a%.0s' $(seq 201))"
assert_eq "$CODE" 1
assert_contains "$OUT" "install.sh: --reference: reference is 201 characters"
[ -e "$H/.config/dumptriage" ] && fail "the install went ahead"
done_test

setup two_hundred_characters_count_code_points_not_bytes
install_ --reference "$(printf 'é%.0s' $(seq 200))"  # 400 bytes in UTF-8
assert_eq "$CODE" 0
done_test

setup the_agent_refuses_a_hand_edited_label_that_is_too_long
install_
python3 - "$H/.config/dumptriage/config.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1])); cfg["reference"] = "a" * 201; json.dump(cfg, open(sys.argv[1], "w"))
PY
OUT=$(env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$H" "$H/.local/bin/dumptriage-agent" status 2>&1); CODE=$?
assert_eq "$CODE" 78
assert_contains "$OUT" "reference is 201 characters"
# and a reinstall that would keep it says so before it changes anything
install_ --policy AlwaysSubmit
assert_eq "$CODE" 1
assert_contains "$OUT" "config.json: reference is 201 characters"
assert_eq "$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["policy"]["mode"])' "$H/.config/dumptriage/config.json")" "AskFirst"
done_test

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]

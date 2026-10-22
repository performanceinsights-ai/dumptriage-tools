#!/bin/bash
# Tests for install.sh's --with-perfetto (dumptriage-tools#28): the pinned download is verified before anything is installed.
# The installer's own function is run against a file:// "release" and a test pin; install.sh itself is not run, since it would
# set up a real service. Run: bash linux/tests/test_install_perfetto.sh
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
INSTALL="$HERE/../dumptriage-agent/installer/install.sh"
PIN="$HERE/../dumptriage-agent/installer/perfetto.pin"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/pf-test.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
PASSED=0 FAILED=0

fail() { printf 'FAIL %s: %s\n' "$CURRENT" "$1"; FAILED=$((FAILED + 1)); CURRENT_FAILED=1; }
assert_contains() { case "$1" in *"$2"*) ;; *) fail "expected '$2' in:
$1" ;; esac; }
assert_not_contains() { case "$1" in *"$2"*) fail "did not expect '$2' in:
$1" ;; esac; }
assert_eq() { [ "$1" = "$2" ] || fail "expected '$2', got '$1'"; }
done_test() { if [ "${CURRENT_FAILED:-0}" -eq 0 ]; then printf 'ok   %s\n' "$CURRENT"; PASSED=$((PASSED + 1)); fi; }

# A release archive with a fake tracebox in it, and a pin that names it.
mkzip() { # dir [member-name]
  python3 - "$1" "${2:-linux-amd64/tracebox}" <<'PY'
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1] + "/release.zip", "w")
z.writestr(sys.argv[2], '#!/bin/sh\necho "Perfetto v0-test (fake)"\n')
z.writestr("linux-amd64/trace_processor_shell", "not wanted")
z.close()
PY
}
setup() {
  CURRENT=$1 CURRENT_FAILED=0
  T="$WORK/$1"; mkdir -p "$T/lib"
  mkzip "$T"
  SHA=$(sha256sum "$T/release.zip" | cut -d' ' -f1)
  printf 'version=v0-test\nurl_amd64=file://%s/release.zip\nsha256_amd64=%s\nurl_arm64=file://%s/release.zip\nsha256_arm64=%s\n' "$T" "$SHA" "$T" "$SHA" >"$T/pin"
}
run_install() { # [env...] : runs the installer's function with die and the test pin; sets OUT and CODE
  OUT=$(env DT_PERFETTO_PIN="$T/pin" "$@" bash -c '
    set -euo pipefail
    die() { echo "install.sh: $*" >&2; exit 1; }
    here=$(dirname "$0"); lib=$LIBDIR; dry=${DRY:-0}
    eval "$(sed -n "/^install_tracebox()/,/^}/p" "$0")"
    install_tracebox' "$INSTALL" 2>&1)
  CODE=$?
}

setup installs_the_pinned_tracebox_when_the_checksum_matches
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 0
assert_contains "$OUT" "tracebox installed at $T/lib/tracebox: Perfetto v0-test (fake)"
[ -x "$T/lib/tracebox" ] || fail "not executable"
[ ! -e "$T/lib/trace_processor_shell" ] || fail "installed more than tracebox"
done_test

setup refuses_a_download_whose_checksum_is_not_the_pinned_one
sed -i "s/^sha256_amd64=.*/sha256_amd64=$(printf '0%.0s' $(seq 64))/; s/^sha256_arm64=.*/sha256_arm64=$(printf '0%.0s' $(seq 64))/" "$T/pin"
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 1
assert_contains "$OUT" "not the pinned"
assert_contains "$OUT" "not installing it"
[ ! -e "$T/lib/tracebox" ] || fail "installed a download that failed its checksum"
done_test

setup refuses_a_tampered_archive_with_the_right_name
echo tampered >>"$T/release.zip"
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 1
assert_contains "$OUT" "not the pinned"
[ ! -e "$T/lib/tracebox" ] || fail "installed a tampered archive"
done_test

setup refuses_an_archive_without_tracebox
mkzip "$T" "linux-amd64/other"
SHA=$(sha256sum "$T/release.zip" | cut -d' ' -f1); sed -i "s/^sha256_.*=.*/&/; s/^sha256_amd64=.*/sha256_amd64=$SHA/; s/^sha256_arm64=.*/sha256_arm64=$SHA/" "$T/pin"
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 1
assert_contains "$OUT" "the archive has no tracebox"
done_test

setup refuses_a_download_that_fails
sed -i "s#^url_amd64=.*#url_amd64=file://$T/missing.zip#; s#^url_arm64=.*#url_arm64=file://$T/missing.zip#" "$T/pin"
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 1
assert_contains "$OUT" "download of file://$T/missing.zip failed"
done_test

setup refuses_a_malformed_pin
printf 'version=v0\nurl_amd64=https://example.invalid/x.zip\nsha256_amd64=nothex\nurl_arm64=https://example.invalid/x.zip\nsha256_arm64=nothex\n' >"$T/pin"
run_install LIBDIR="$T/lib"
assert_eq "$CODE" 1
assert_contains "$OUT" "has no usable url_"
done_test

setup a_dry_run_downloads_nothing
run_install LIBDIR="$T/lib" DRY=1
assert_eq "$CODE" 0
assert_contains "$OUT" "+ download file://$T/release.zip, require sha256 $SHA, install tracebox into $T/lib"
[ ! -e "$T/lib/tracebox" ] || fail "a dry run installed it"
done_test

CURRENT=the_shipped_pin_is_well_formed CURRENT_FAILED=0
for arch in amd64 arm64; do
  url=$(sed -n "s/^url_$arch=//p" "$PIN"); sha=$(sed -n "s/^sha256_$arch=//p" "$PIN")
  case $url in https://github.com/google/perfetto/releases/download/v*/linux-$arch.zip) ;; *) fail "$arch url is not a google/perfetto release asset: $url" ;; esac
  [[ $sha =~ ^[0-9a-f]{64}$ ]] || fail "$arch sha256 is not 64 hex digits"
done
version=$(sed -n 's/^version=//p' "$PIN")
assert_contains "$(sed -n 's/^url_amd64=//p' "$PIN")" "/download/$version/"
assert_contains "$(sed -n 's/^url_arm64=//p' "$PIN")" "/download/$version/"
done_test

CURRENT=install_sh_lists_the_option_and_still_parses CURRENT_FAILED=0
bash -n "$INSTALL" || fail "syntax"
assert_contains "$(bash "$INSTALL" --help)" "--with-perfetto"
OUT=$(bash "$INSTALL" --dry-run --method cron --with-perfetto 2>&1); 
assert_contains "$OUT" "+ download https://github.com/google/perfetto/releases/download/"
assert_contains "$OUT" "installer/../perfetto/flight-recorder.pbtx"
done_test

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]

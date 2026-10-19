#!/bin/bash
# dumptriage-collect.sh: send recent macOS crash reports, kernel panics and
# spin/hang reports to Dump Triage (website#1746).
#
# macOS writes these itself, to ~/Library/Logs/DiagnosticReports (your own
# apps) and /Library/Logs/DiagnosticReports (system processes and kernel
# panics): .ips (app crashes, and panics on macOS 12+), .panic, .spin, .hang.
# This lists the recent ones, lets you pick, shows exactly which files will be
# sent and asks before sending anything. It can also take a spindump of a
# process that is hanging right now (--spindump, needs sudo).
#
# Works with the bash 3.2 and the tools macOS ships (curl, find, security,
# shasum, spindump); nothing to install.
#
# Usage: dumptriage-collect.sh [--days N] [--all] [--spindump PID|NAME] [--dry-run] [--dir DIR]...
#
#   --days N       list reports from the last N days (default 7)
#   --all          select every listed report instead of asking which
#   --spindump X   sample process X (pid or name) for 10 s now and send that; with --all, the listed reports too
#   --dry-run      show what would be sent, send nothing (no key or URL needed)
#   --dir DIR      look in DIR instead of the two DiagnosticReports folders (repeatable)
#
# Environment:
#   DUMPTRIAGE_API_URL     the service, e.g. https://dumptriage.example.com (required to send)
#   DUMPTRIAGE_API_KEY     your API key; if unset, read from the login keychain:
#                          security add-generic-password -s dumptriage -a "$USER" -w
#   DUMPTRIAGE_PORTAL_URL  where report links point (defaults to DUMPTRIAGE_API_URL)

set -u

DAYS=7
SEND_ALL=0
DRY_RUN=0
SPINDUMP_TARGET=""
DIRS=()
SPINDUMP_SECONDS=10

usage() {
    sed -n '2,/^$/s/^# \{0,1\}//p' "$0"
}

die() {
    printf 'dumptriage-collect: %s\n' "$1" >&2
    exit "${2:-1}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --days)
            [ $# -ge 2 ] || die "--days needs a number" 2
            case "$2" in ''|*[!0-9]*) die "--days needs a number, not '$2'" 2 ;; esac
            DAYS=$2; shift 2 ;;
        --all) SEND_ALL=1; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        --spindump)
            [ $# -ge 2 ] || die "--spindump needs a process id or name" 2
            SPINDUMP_TARGET=$2; shift 2 ;;
        --dir)
            [ $# -ge 2 ] || die "--dir needs a directory" 2
            DIRS[${#DIRS[@]}]=$2; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1' (see --help)" 2 ;;
    esac
done

if [ ${#DIRS[@]} -eq 0 ]; then
    DIRS=("$HOME/Library/Logs/DiagnosticReports" "/Library/Logs/DiagnosticReports")
fi

# What a report is, from its name and, for an .ips, its first line's bug_type
# (Apple's OSAnalytics log types). Prints crash, panic or hang; nothing for a
# report Dump Triage does not take (jetsam events, analytics, diagnostics).
report_kind() {
    case "$1" in
        *.panic) echo panic; return ;;
        *.spin|*.hang) echo hang; return ;;
        *.ips) ;;
        *) return ;;
    esac
    bug_type=$(head -n 1 "$1" 2>/dev/null | sed -n 's/.*"bug_type"[[:space:]]*:[[:space:]]*"\{0,1\}\([0-9][0-9]*\).*/\1/p')
    case "$bug_type" in
        309|109) echo crash ;;
        210|110|410) echo panic ;;
        288|409|11|12|28|29) echo hang ;;
    esac
}

file_size() {
    wc -c < "$1" | tr -d ' '
}

# Recent reports, newest first: parallel arrays FILES and KINDS.
FILES=()
KINDS=()
collect() {
    listing=$(
        for dir in "${DIRS[@]}"; do
            [ -d "$dir" ] || continue
            find "$dir" -maxdepth 1 -type f \( -name '*.ips' -o -name '*.panic' -o -name '*.spin' -o -name '*.hang' \) \
                -mtime "-$DAYS" -print 2>/dev/null
        done
    )
    [ -n "$listing" ] || return 0
    # ls -t sorts newest first; one name per line (DiagnosticReports names have no newlines).
    old_ifs=$IFS
    IFS='
'
    set -f  # a [ or * in a report's name is part of the name, not a pattern
    # shellcheck disable=SC2086 # split on newlines only, on purpose
    sorted=$(ls -t $listing 2>/dev/null)
    for f in $sorted; do
        kind=$(report_kind "$f")
        [ -n "$kind" ] || continue
        FILES[${#FILES[@]}]=$f
        KINDS[${#KINDS[@]}]=$kind
    done
    set +f
    IFS=$old_ifs
}

take_spindump() {
    out="${TMPDIR:-/tmp}/dumptriage-spindump-$$.txt"
    printf 'Sampling %s for %s seconds with spindump (sudo asks for your password)...\n' "$SPINDUMP_TARGET" "$SPINDUMP_SECONDS"
    if ! sudo spindump "$SPINDUMP_TARGET" "$SPINDUMP_SECONDS" -file "$out" >/dev/null; then
        die "spindump of '$SPINDUMP_TARGET' failed"
    fi
    [ -s "$out" ] || die "spindump wrote nothing to $out"
    SPINDUMP_FILE=$out
}

SELECTED=()
SELECTED_KINDS=()
select_files() {
    count=${#FILES[@]}
    if [ "$count" -eq 0 ]; then
        return 0
    fi
    echo "Reports from the last $DAYS day(s), newest first:"
    i=0
    while [ $i -lt "$count" ]; do
        printf '  %2d) %-6s %s (%s bytes)\n' $((i + 1)) "${KINDS[$i]}" "${FILES[$i]}" "$(file_size "${FILES[$i]}")"
        i=$((i + 1))
    done
    if [ "$SEND_ALL" -eq 1 ]; then
        choice=all
    else
        printf 'Which to send? Numbers separated by spaces, "all", or Enter for none: '
        read -r choice || choice=""
    fi
    case "$choice" in
        a|all|ALL) choice=$(i=1; while [ $i -le "$count" ]; do printf '%s ' $i; i=$((i + 1)); done) ;;
    esac
    for n in $choice; do
        case "$n" in
            ''|*[!0-9]*) die "'$n' is not a number from the list" 2 ;;
        esac
        if [ "$n" -lt 1 ] || [ "$n" -gt "$count" ]; then
            die "$n is not in the list (1-$count)" 2
        fi
        SELECTED[${#SELECTED[@]}]=${FILES[$((n - 1))]}
        SELECTED_KINDS[${#SELECTED_KINDS[@]}]=${KINDS[$((n - 1))]}
    done
}

api_key() {
    if [ -n "${DUMPTRIAGE_API_KEY:-}" ]; then
        printf '%s' "$DUMPTRIAGE_API_KEY"
        return 0
    fi
    if command -v security >/dev/null 2>&1; then
        security find-generic-password -s dumptriage -w 2>/dev/null && return 0
    fi
    return 1
}

# One upload: curl reads the Authorization header from its config on stdin, so
# the key never appears in the process list. -H 'Expect:' stops curl adding
# 'Expect: 100-continue' to a large upload, which the API refuses (website#1801).
upload() {
    file=$1
    sha_header=""
    if command -v shasum >/dev/null 2>&1; then
        sha=$(shasum -a 256 "$file" | cut -d ' ' -f 1)
        sha_header="X-Content-SHA256: $sha"
    fi
    response=$(
        {
            printf 'header = "Authorization: Bearer %s"\n' "$KEY"
            [ -n "$sha_header" ] && printf 'header = "%s"\n' "$sha_header"
        } | curl -sS -K - -H 'Expect:' -F "file=@$file" -w '\n%{http_code}' "${API_URL%/}/api/v1/dumps"
    )
    status=$(printf '%s\n' "$response" | tail -n 1)
    body=$(printf '%s\n' "$response" | sed '$d')
    case "$status" in
        200|201)
            id=$(printf '%s' "$body" | sed -n 's/^[^"]*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            printf 'sent %s\n  report: %s/dumps/%s\n' "$file" "${PORTAL_URL%/}" "$id"
            return 0 ;;
        *)
            detail=$(printf '%s' "$body" | sed -n 's/.*"detail"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            printf 'NOT sent %s: HTTP %s %s\n' "$file" "${status:-(no response)}" "${detail:-$body}" >&2
            return 1 ;;
    esac
}

collect
SPINDUMP_FILE=""
if [ -n "$SPINDUMP_TARGET" ]; then
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "Would take a spindump of $SPINDUMP_TARGET ($SPINDUMP_SECONDS s, sudo) and send it."
    else
        take_spindump
    fi
fi

if [ ${#FILES[@]} -eq 0 ] && [ -z "$SPINDUMP_TARGET" ]; then
    echo "No crash, panic or hang reports from the last $DAYS day(s) in: ${DIRS[*]}"
    exit 0
fi
if [ ${#FILES[@]} -gt 0 ] && { [ -z "$SPINDUMP_TARGET" ] || [ "$SEND_ALL" -eq 1 ]; }; then
    select_files
fi
if [ -n "$SPINDUMP_FILE" ]; then
    SELECTED[${#SELECTED[@]}]=$SPINDUMP_FILE
    SELECTED_KINDS[${#SELECTED_KINDS[@]}]=hang
fi
if [ ${#SELECTED[@]} -eq 0 ]; then
    [ "$DRY_RUN" -eq 1 ] && [ -n "$SPINDUMP_TARGET" ] && exit 0
    echo "Nothing selected; nothing sent."
    exit 0
fi

echo
echo "These files will be sent:"
i=0
while [ $i -lt ${#SELECTED[@]} ]; do
    printf '  %-6s %s (%s bytes)\n' "${SELECTED_KINDS[$i]}" "${SELECTED[$i]}" "$(file_size "${SELECTED[$i]}")"
    i=$((i + 1))
done
echo "They can contain paths with your user name, app names and versions; Dump Triage redacts user names before analysis."

if [ "$DRY_RUN" -eq 1 ]; then
    echo "Dry run: nothing sent."
    exit 0
fi

API_URL=${DUMPTRIAGE_API_URL:-}
[ -n "$API_URL" ] || die "set DUMPTRIAGE_API_URL to the service's address, e.g. https://dumptriage.example.com" 2
PORTAL_URL=${DUMPTRIAGE_PORTAL_URL:-$API_URL}
KEY=$(api_key) || die "no API key: set DUMPTRIAGE_API_KEY, or store it with: security add-generic-password -s dumptriage -a \"\$USER\" -w" 2

printf 'Send %d file(s) to %s? [y/N] ' ${#SELECTED[@]} "$API_URL"
read -r answer || answer=""
case "$answer" in
    y|Y|yes|YES) ;;
    *) echo "Not sent."; exit 0 ;;
esac

failed=0
for f in "${SELECTED[@]}"; do
    upload "$f" || failed=1
done
[ -n "$SPINDUMP_FILE" ] && rm -f "$SPINDUMP_FILE"
exit $failed

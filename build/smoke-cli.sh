#!/usr/bin/env bash
# Smoke-tests an installed cdp against the fixture site in the repository: check, batch, chrome, snapshot and run.
# Usage: build/smoke-cli.sh <command that runs cdp>...
#   build/smoke-cli.sh ./tool/cdp
#   build/smoke-cli.sh dotnet dnx Nullean.Cdp.Cli@1.2.3 --add-source ./feed --yes
# Runs on bash 3.2 (macOS) and Git Bash (Windows). Needs a Chrome, which GitHub's runner images have.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <command that runs cdp>..." >&2
  exit 2
fi

site="$(cd "$(dirname "$0")/../tests/Nullean.Cdp.Tests/site" && pwd)"
# A native Windows binary does not understand Git Bash's /d/a/... paths.
if command -v cygpath > /dev/null; then site="$(cygpath -w "$site")"; fi
journey="$site/journeys/everything.journey"
about="$site/about.html"
bad="$(mktemp -d)"
badjourney="$bad/bad.journey"
if command -v cygpath > /dev/null; then badjourney="$(cygpath -w "$badjourney")"; fi
trap 'rm -rf "$bad"' EXIT
printf 'open /\nscreenshot x.png\n' > "$bad/bad.journey"

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

# Runs cdp with the given arguments; sets $out and $code.
run() {
  set +e
  out="$("$@" 2>&1)"
  code=$?
  set -e
  printf '$ %s\n%s\n[exit %s]\n' "$*" "$(printf '%s' "$out" | head -c 2000)" "$code"
}

run "$@" check "$journey"
[ "$code" -eq 0 ] || fail "check should exit 0"
case "$out" in *"40 steps, 16 checkpoints"*) ;; *) fail "check should count the journey's steps" ;; esac

run "$@" check "$badjourney"
[ "$code" -eq 2 ] || fail "check should exit 2 on a journey that does not parse"
case "$out" in *"bad.journey:2: 'screenshot' is not supported"*) ;; *) fail "check should name the line" ;; esac

run "$@" batch "$journey" --base-url http://localhost:5000/
[ "$code" -eq 0 ] || fail "batch should exit 0"
case "$out" in *'["open","http://localhost:5000/actions.html"]'*) ;; *) fail "batch should resolve open URLs" ;; esac

run "$@" chrome
[ "$code" -eq 0 ] || fail "chrome should find a Chrome"

run "$@" snapshot "$about" --compact
[ "$code" -eq 0 ] || fail "snapshot should exit 0"
case "$out" in *'heading "About us" [level=1]'*) ;; *) fail "snapshot should print the tree" ;; esac
case "$out" in *"--- END_PAGE_CONTENT nonce="*) ;; *) fail "snapshot should fence the page content" ;; esac

run "$@" run "$journey" --base-url "$site"
[ "$code" -eq 0 ] || fail "run should pass the everything journey"
case "$out" in *"40 steps, 16 checkpoints"*) ;; *) fail "run should report the journey" ;; esac

echo "SMOKE OK"

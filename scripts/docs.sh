#!/usr/bin/env bash
# Build the documentation as CI does, then serve it: docs-builder with the design-system flag, then
# scripts/docs-finish.sh (landing page, theme, fonts). `docs-builder serve` renders on demand and never applies those,
# so it would preview the wrong home page in the wrong theme. Adapted from nullean/snapshots'.
#
#   scripts/docs.sh            build and serve on http://localhost:4000
#   scripts/docs.sh --noserve  build only
#
# docs-builder is elastic/docs-builder's native binary: install it yourself and point DOCS_BUILDER at it, or put it on
# the PATH. This script does not download it.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

tool="${DOCS_BUILDER:-$(command -v docs-builder || true)}"
if [[ -z "$tool" || ! -x "$tool" ]]; then
  echo "docs: docs-builder not found; install it from https://github.com/elastic/docs-builder/releases and set DOCS_BUILDER" >&2
  exit 2
fi

# The design-system layout is behind a feature flag, read from the environment only.
export FEATURE_NAVIGATION_PREVIEW=true
"$tool" build --path docs
out=".artifacts/docs/html"
scripts/docs-finish.sh "$out"

[[ "${1:-}" == "--noserve" ]] && exit 0
echo "docs: serving http://localhost:4000"
cd "$out" && exec python3 -m http.server 4000

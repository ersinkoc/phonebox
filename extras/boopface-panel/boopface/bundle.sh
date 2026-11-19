#!/usr/bin/env bash
# Rebuild the inlined avatar bundle from the published package.
# boopface.iife.js is committed so the panel needs no network; this regenerates it.
set -euo pipefail
cd "$(dirname "$0")"
tmp="$(mktemp -d)"
npm pack boopface@0.8.0 --pack-destination "$tmp" >/dev/null
tar xzf "$tmp"/boopface-0.8.0.tgz -C "$tmp"
npx --yes esbuild@0.23.1 "$tmp/package/boopface.js" \
  --bundle --format=iife --global-name=BoopFace --minify --legal-comments=none \
  --outfile=boopface.iife.js
echo "wrote $(wc -c < boopface.iife.js) bytes"

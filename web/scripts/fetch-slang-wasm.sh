#!/usr/bin/env bash
# Fetch the slang-wasm release artifact and unpack it into web/public/slang/.
#
# The .wasm file is ~22MB which is too large to track in git. CI (and any
# fresh checkout) must run this once before `npm run dev` / `npm run build`.
#
# Pinned version: edit SLANG_VER below to bump. The interface.d.ts ABI changes
# between releases — re-run TypeScript checks after bumping.
set -euo pipefail

# Pinned to the release Phase 6 was tested against. Bump deliberately.
SLANG_VER="${SLANG_VER:-v2026.8.1}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${SCRIPT_DIR}/../public/slang"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

mkdir -p "${DEST}"

# Release asset name pattern: slang-<version-without-v>-wasm.zip
ver_no_v="${SLANG_VER#v}"
URL="https://github.com/shader-slang/slang/releases/download/${SLANG_VER}/slang-${ver_no_v}-wasm.zip"

echo "[fetch-slang-wasm] downloading ${URL}"
curl -fsSL -o "${TMP}/slang-wasm.zip" "${URL}"

echo "[fetch-slang-wasm] unpacking into ${DEST}"
unzip -o -q "${TMP}/slang-wasm.zip" -d "${TMP}"
cp "${TMP}/slang-wasm.js"   "${DEST}/slang-wasm.js"
cp "${TMP}/slang-wasm.wasm" "${DEST}/slang-wasm.wasm"
cp "${TMP}/interface.d.ts"  "${DEST}/interface.d.ts"

echo "[fetch-slang-wasm] done (version ${SLANG_VER})"
ls -la "${DEST}"

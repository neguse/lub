#!/usr/bin/env bash
# Fetch the slang-wasm release artifact and unpack it into web/public/slang/.
#
# The .wasm file is ~22MB which is too large to track in git. CI (and any
# fresh checkout) must run this once before `npm run dev` / `npm run build`.
# It is wired as `npm run fetch-slang` (and as a `postinstall` hook on the web
# package) so a fresh `npm install` automatically vendors slang-wasm.
#
# Pinned version: edit SLANG_VER below to bump. The interface.d.ts ABI changes
# between releases — re-run TypeScript checks after bumping.
set -euo pipefail

# Pinned to the release Phase 6 was tested against. Bump deliberately.
SLANG_VER="${SLANG_VER:-v2026.8.1}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${SCRIPT_DIR}/../public/slang"

# Idempotent guard: if slang-wasm.wasm is already vendored with non-zero
# size, skip the download. The postinstall hook fires on every `npm install`
# so without this check fresh installs would re-download ~10MB every time.
# Force a re-fetch by deleting web/public/slang/slang-wasm.wasm or setting
# SGLUA_FETCH_SLANG_FORCE=1.
WASM_PATH="${DEST}/slang-wasm.wasm"
if [[ -z "${SGLUA_FETCH_SLANG_FORCE:-}" ]] && [[ -s "${WASM_PATH}" ]]; then
  echo "[fetch-slang-wasm] already vendored (${WASM_PATH}); set SGLUA_FETCH_SLANG_FORCE=1 to refetch"
  exit 0
fi

# Verify required tooling is available before tearing down a temp dir we'd
# have to clean up anyway. unzip is the one most commonly missing on minimal
# CI containers — fail with a self-explanatory message.
for tool in curl unzip; do
  if ! command -v "${tool}" >/dev/null 2>&1; then
    echo "[fetch-slang-wasm] ERROR: '${tool}' is not installed." >&2
    echo "  Install it (e.g. 'sudo apt install ${tool}' or 'sudo pacman -S ${tool}') and re-run." >&2
    exit 1
  fi
done

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

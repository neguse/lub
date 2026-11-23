#!/usr/bin/env bash
# check / conversion helper for C# (TinyC#) samples (mainly for CI and gates).
# Interactive runs are supported first-class by the lub CLI:
#   ./build/lub samples/<name>/<Entry>.csproj   # tcs vm --watch + hot reload
#
# Usage:
#   scripts/run-cs-sample.sh <sample> --check   # tcs check (diagnostics only. Fails if there are warnings)
#   scripts/run-cs-sample.sh <sample> --vm      # build tcs VM bytecode with tcs vm (samples/<sample>/.lub/<Entry>.tcsb. Run it with lub)
#
# The entry class is the basename of <Entry>.csproj (if missing, the basename of the only .cs).
# The inputs are all *.cs under the same directory (excluding bin / obj / hidden directories) + the cs-lib implementation sources (same convention as the lub CLI).
# Requirements: dotnet SDK
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="${1:?usage: run-cs-sample.sh <sample> --check|--vm}"
MODE="${2:?usage: run-cs-sample.sh <sample> --check|--vm}"

DIR="samples/$NAME"
if [[ ! -d "$DIR" ]]; then
    echo "sample not found: $DIR" >&2
    exit 1
fi
if [[ ! -f tcs/Transpiler/Transpiler.csproj ]]; then
    echo "tcs/Transpiler/Transpiler.csproj not found (run from the repo root)" >&2
    exit 1
fi

mapfile -t CS_FILES < <(find "$DIR" \( -name bin -o -name obj -o -name '.?*' \) -prune \
    -o -name '*.cs' -print | sort)
if [[ ${#CS_FILES[@]} -eq 0 ]]; then
    echo "no .cs in $DIR" >&2
    exit 1
fi
mapfile -t CSPROJ < <(find "$DIR" -maxdepth 1 -name '*.csproj' | sort)
if [[ ${#CSPROJ[@]} -ge 1 ]]; then
    ENTRY_CLASS="$(basename "${CSPROJ[0]}" .csproj)"
elif [[ ${#CS_FILES[@]} -eq 1 ]]; then
    ENTRY_CLASS="$(basename "${CS_FILES[0]}" .cs)"
else
    echo "multiple .cs but no .csproj in $DIR (entry class ambiguous)" >&2
    exit 1
fi

# Add all the cs-lib implementation sources (lub_stub.cs is excluded because it is --ref only)
mapfile -t -O "${#CS_FILES[@]}" CS_FILES \
    < <(find cs-lib -name '*.cs' ! -name 'lub_stub.cs' | sort)

# dotnet run evaluates MSBuild on every call and takes ~5 seconds each time.
# If the caller (native-gate.sh) builds Transpiler once and passes LUB_TCS_DLL, the DLL is run directly from then on and the startup cost is not paid.
if [[ -n "${LUB_TCS_DLL:-}" ]]; then
    TCS=(dotnet "$LUB_TCS_DLL")
else
    TCS=(dotnet run --project tcs/Transpiler --)
fi

case "$MODE" in
--check)
    exec "${TCS[@]}" check "${CS_FILES[@]}" --ref cs-lib/lub_stub.cs
    ;;
--vm)
    mkdir -p "$DIR/.lub"
    exec "${TCS[@]}" vm "${CS_FILES[@]}" --ref cs-lib/lub_stub.cs --export "$ENTRY_CLASS.*" \
        -o "$DIR/.lub/$ENTRY_CLASS.tcsb"
    ;;
*)
    echo "unknown mode: $MODE (--check | --vm)" >&2
    exit 2
    ;;
esac

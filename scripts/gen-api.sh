#!/usr/bin/env bash
# Generates files from cs-lib/lub_stub.cs (the API description).
#   include/lub/lub_api.h        C API header
#   src/gen/vm_api_gen.c         host functions of the tcs VM
#   web/gen/lub-api-docs.json    API reference data (read by web/scripts/gen-api-docs.mjs)
#   dotnet/Lub/Lub.g.cs          facade for .NET execution (P/Invoke)
#   dotnet/Lub/Lubx.g.cs         facade for .NET execution of namespace Lubx (the C implementation used by lubx)
# --check does not regenerate; it checks the diff against the checked-in generated files (native gate).
set -euo pipefail
cd "$(dirname "$0")/.."
check=0
[ "${1:-}" = "--check" ] && check=1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
# lub-gen is built once and its DLL is run directly (dotnet run evaluates MSBuild on every call)
dotnet build tools/lub-gen -nologo -v q -o "$tmp/lub-gen" > /dev/null
gen=(dotnet "$tmp/lub-gen/lub-gen.dll")
"${gen[@]}" check
"${gen[@]}" header -o "$tmp/lub_api.h"
"${gen[@]}" vm -o "$tmp/vm_api_gen.c"
"${gen[@]}" docs -o "$tmp/lub-api-docs.json"
"${gen[@]}" facade -o "$tmp/Lub.g.cs"
"${gen[@]}" facade-lubx -o "$tmp/Lubx.g.cs"
# The checked-in generated files are kept in the form produced by the same formatting as scripts/format.sh.
# The formatter sometimes does not reach a fixed point in one pass (wrapping of CJK comments, etc.), so run it until nothing changes.
for _ in 1 2 3 4; do
  cp "$tmp/lub_api.h" "$tmp/h.prev"; cp "$tmp/vm_api_gen.c" "$tmp/v.prev"
  clang-format --style=file:.clang-format -i "$tmp/lub_api.h" "$tmp/vm_api_gen.c"
  cmp -s "$tmp/lub_api.h" "$tmp/h.prev" && cmp -s "$tmp/vm_api_gen.c" "$tmp/v.prev" && break
done
status=0
for pair in "lub_api.h:include/lub/lub_api.h" "vm_api_gen.c:src/gen/vm_api_gen.c" "lub-api-docs.json:web/gen/lub-api-docs.json" "Lub.g.cs:dotnet/Lub/Lub.g.cs" "Lubx.g.cs:dotnet/Lub/Lubx.g.cs"; do
  src="$tmp/${pair%%:*}"
  dst="${pair#*:}"
  if [ "$check" = 1 ]; then
    if ! diff -q "$src" "$dst" > /dev/null; then
      # For C files, line wrapping changes with the clang-format version.
      # That is not a difference in content, so pass if they are the same with whitespace removed (other generated files require an exact match).
      case "$dst" in *.h|*.c) ws_tolerant=1 ;; *) ws_tolerant=0 ;; esac
      if [ "$ws_tolerant" = 1 ] && [ "$(tr -d '[:space:]' < "$src" | sha256sum)" = "$(tr -d '[:space:]' < "$dst" | sha256sum)" ]; then
        echo "gen-api: $dst differs only in formatting (formatter version); ok"
      else
        echo "gen-api: $dst is stale (run scripts/gen-api.sh)"
        status=1
      fi
    fi
  else
    cp "$src" "$dst"
    echo "gen-api: wrote $dst"
  fi
done
exit $status

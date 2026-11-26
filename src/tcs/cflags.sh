#!/usr/bin/env bash
# Prints the C compiler flags for the C that `tcs c` generates (one per line): the floating-point rules of docs/tcs/il-spec.md appendix B.
# -fexcess-precision=standard is left out for a compiler (CC, default cc) that does not accept it.
set -euo pipefail
printf '%s\n' -O2 -ffp-contract=off -fwrapv
probe="$(mktemp -d)"
trap 'rm -rf "$probe"' EXIT
printf 'int main(void){return 0;}\n' > "$probe/probe.c"
if "${CC:-cc}" -fexcess-precision=standard -Werror -c "$probe/probe.c" -o "$probe/probe.o" >/dev/null 2>&1; then
  echo -fexcess-precision=standard
fi

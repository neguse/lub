#!/usr/bin/env bash
# usage: tlc.sh <Module> <name> <expect: OK|INVARIANT|TEMPORAL> <constants...> -- <INVARIANTS ...> <PROPERTIES ...>
# <name>.cfg を書いて TLC を回し、期待する判定と一致しなければ exit 1 (self-check)。出力は <name>.out に残す。
# TLC_JAR と JAVA は run.sh が決める。
set -u
mod=$1; name=$2; expect=$3; shift 3
cfg="$name.cfg"
{
  echo "SPECIFICATION Spec"
  echo "CONSTANTS"
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do echo "  $1"; shift; done
  [ $# -gt 0 ] && shift
  printf '%s\n' "$@"
} > "$cfg"
read -ra java_cmd <<< "${JAVA:-java}"
"${java_cmd[@]}" -XX:+UseParallelGC -cp "$TLC_JAR" tlc2.TLC \
  -workers auto -deadlock -config "$cfg" "$mod.tla" > "$name.out" 2>&1
if grep -q "No error has been found" "$name.out"; then got=OK
elif grep -q "is violated" "$name.out" && grep -q "Invariant" "$name.out"; then got=INVARIANT
elif grep -q "Temporal properties were violated" "$name.out"; then got=TEMPORAL
else got=ERROR; fi
states=$(grep -o '[0-9]* distinct states found' "$name.out" | tail -1)
if [ "$got" = "$expect" ]; then echo "PASS  $name: $got ($states)"; exit 0
else echo "FAIL  $name: expected $expect, got $got"; tail -20 "$name.out"; exit 1; fi

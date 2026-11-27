#!/usr/bin/env bash
# Prints the arguments that give tcs its own source (one per line, paths from the repo root): the declarations as --ref, then Compiler, CBackend, VmBackend, Api and Cli.
# Api/TcsFiles.cs and Cli/CliProcess.cs only declare what tcs asks of the outside; the program that runs tcs implements them (Native/, Dotnet/).
set -euo pipefail
cd "$(dirname "$0")/../.."
# The order of the files does not depend on the locale
export LC_ALL=C
refs=(src/tcs/Api/TcsFiles.cs src/tcs/Cli/CliProcess.cs)
for r in "${refs[@]}"; do
  echo --ref
  echo "$r"
done
for f in src/tcs/Compiler/*.cs src/tcs/CBackend/*.cs src/tcs/VmBackend/*.cs src/tcs/Api/*.cs src/tcs/Cli/*.cs; do
  case " ${refs[*]} " in
    *" $f "*) ;;
    *) echo "$f" ;;
  esac
done

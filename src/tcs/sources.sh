#!/usr/bin/env bash
# Prints the arguments that give tcs its own source (one per line, paths from the repo root): --ref Tool/TcsHost.cs, then Compiler, CBackend, VmBackend and Tool.
# TcsHost.cs only declares the OS functions; each host implements them (Native/host.c, Dotnet/DotnetHost.cs).
set -euo pipefail
cd "$(dirname "$0")/../.."
# The order of the files does not depend on the locale
export LC_ALL=C
echo --ref
echo src/tcs/Tool/TcsHost.cs
for f in src/tcs/Compiler/*.cs src/tcs/CBackend/*.cs src/tcs/VmBackend/*.cs src/tcs/Tool/*.cs; do
  [ "$f" = src/tcs/Tool/TcsHost.cs ] || echo "$f"
done

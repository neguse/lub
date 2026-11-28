#!/usr/bin/env bash
# Writes the files generated from cs-lib/lub_stub.cs (the API description): the C API header, the host functions of the tcs VM,
# the API reference data and the facades of .NET execution. The list and the steps are in tests/lub-test/GenApi.cs.
# The test tools.gen-api checks that the checked-in files are up to date.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project tests/lub-test -- gen-api

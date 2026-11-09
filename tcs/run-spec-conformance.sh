#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

TCS_SPEC_CONFORMANCE=1 \
TCS_SPEC_REPORT="$SCRIPT_DIR/doc/spec-conformance-report.md" \
dotnet test "$SCRIPT_DIR" \
  --filter "FullyQualifiedName~SpecConformance"

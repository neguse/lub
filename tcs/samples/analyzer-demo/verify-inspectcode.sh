#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TOOL_VERSION="2026.1.3"
TOOL_DIR="${TCS_JETBRAINS_TOOL_DIR:-/tmp/tcs-jetbrains-tools}"
OUTPUT_DIR="${TCS_INSPECTCODE_OUTPUT_DIR:-/tmp/tcs-inspectcode-analyzer-demo}"
JB="$TOOL_DIR/jb"
PROJECT_REFERENCE_SARIF="$OUTPUT_DIR/project-reference.sarif"
PROJECT_REFERENCE_STDOUT_LOG="$OUTPUT_DIR/project-reference.stdout"
PACKAGE_DIR="$OUTPUT_DIR/local-nupkg"
PACKAGE_CONSUMER_DIR="$OUTPUT_DIR/package-consumer"
PACKAGE_CONSUMER_PROJECT="$PACKAGE_CONSUMER_DIR/analyzer-package-consumer.csproj"
PACKAGE_REFERENCE_SARIF="$OUTPUT_DIR/package-reference.sarif"
PACKAGE_REFERENCE_STDOUT_LOG="$OUTPUT_DIR/package-reference.stdout"
PACKAGE_REFERENCE_OVERRIDE_SARIF="$OUTPUT_DIR/package-reference-severity-override.sarif"
PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG="$OUTPUT_DIR/package-reference-severity-override.stdout"

if [ ! -x "$JB" ]; then
  mkdir -p "$TOOL_DIR"
  dotnet tool install JetBrains.ReSharper.GlobalTools \
    --tool-path "$TOOL_DIR" \
    --version "$TOOL_VERSION"
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR" "$PACKAGE_DIR" "$PACKAGE_CONSUMER_DIR"

run_inspectcode() {
  local project="$1"
  local sarif="$2"
  local stdout_log="$3"

  "$JB" inspectcode "$project" \
    --format=Sarif \
    --output="$sarif" \
    --no-updates \
    --verbosity=ERROR \
    >"$stdout_log" 2>&1
}

count_rule() {
  local sarif="$1"
  local rule_id="$2"
  awk -v rule_id="\"ruleId\": \"$rule_id\"" \
    'index($0, rule_id) { count++ } END { print count + 0 }' \
    "$sarif"
}

assert_expected_counts() {
  local label="$1"
  local sarif="$2"
  local stdout_log="$3"
  local tcs1001_count
  local tcs1002_count
  local tcs1003_count

  tcs1001_count="$(count_rule "$sarif" TCS1001)"
  tcs1002_count="$(count_rule "$sarif" TCS1002)"
  tcs1003_count="$(count_rule "$sarif" TCS1003)"

  if [ "$tcs1001_count" -ne 5 ] || [ "$tcs1002_count" -ne 1 ] || [ "$tcs1003_count" -ne 1 ]; then
    echo "Error: InspectCode $label expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count" >&2
    echo "SARIF: $sarif" >&2
    echo "stdout/stderr log: $stdout_log" >&2
    exit 1
  fi

  echo "InspectCode $label diagnostics verified."
  echo "TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count"
  echo "SARIF: $sarif"
  echo "stdout/stderr log: $stdout_log"
}

run_inspectcode \
  "$REPO_ROOT/samples/analyzer-demo/analyzer-demo.csproj" \
  "$PROJECT_REFERENCE_SARIF" \
  "$PROJECT_REFERENCE_STDOUT_LOG"
assert_expected_counts \
  "ProjectReference analyzer demo" \
  "$PROJECT_REFERENCE_SARIF" \
  "$PROJECT_REFERENCE_STDOUT_LOG"

dotnet pack "$REPO_ROOT/TinyCs.Analyzers/TinyCs.Analyzers.csproj" \
  -c Release \
  -o "$PACKAGE_DIR" >/dev/null
cp "$REPO_ROOT/samples/analyzer-demo/Program.cs" "$PACKAGE_CONSUMER_DIR/Program.cs"
cat > "$PACKAGE_CONSUMER_PROJECT" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreAdditionalProjectSources>$PACKAGE_DIR</RestoreAdditionalProjectSources>
    <RestorePackagesPath>$PACKAGE_CONSUMER_DIR/packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TinyCs.Analyzers" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF
cat > "$PACKAGE_CONSUMER_DIR/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = warning
dotnet_diagnostic.TCS1003.severity = warning
EOF
dotnet restore "$PACKAGE_CONSUMER_PROJECT" >/dev/null
run_inspectcode \
  "$PACKAGE_CONSUMER_PROJECT" \
  "$PACKAGE_REFERENCE_SARIF" \
  "$PACKAGE_REFERENCE_STDOUT_LOG"
assert_expected_counts \
  "PackageReference consumer" \
  "$PACKAGE_REFERENCE_SARIF" \
  "$PACKAGE_REFERENCE_STDOUT_LOG"

cat > "$PACKAGE_CONSUMER_DIR/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = error
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = error
EOF
set +e
run_inspectcode \
  "$PACKAGE_CONSUMER_PROJECT" \
  "$PACKAGE_REFERENCE_OVERRIDE_SARIF" \
  "$PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG"
override_exit=$?
set -e
if [ "$override_exit" -eq 0 ]; then
  echo "Error: InspectCode PackageReference severity override expected TCS errors" >&2
  echo "SARIF: $PACKAGE_REFERENCE_OVERRIDE_SARIF" >&2
  echo "stdout/stderr log: $PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG" >&2
  exit 1
fi
override_tcs1001_count="$(awk 'index($0, "TCS1001:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }' "$PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG")"
override_tcs1002_count="$(awk 'index($0, "TCS1002:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }' "$PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG")"
override_tcs1003_count="$(awk 'index($0, "TCS1003:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }' "$PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG")"
if [ "$override_tcs1001_count" -ne 5 ] || [ "$override_tcs1002_count" -ne 1 ] || [ "$override_tcs1003_count" -ne 1 ]; then
  echo "Error: InspectCode PackageReference severity override expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$override_tcs1001_count / TCS1002 x$override_tcs1002_count / TCS1003 x$override_tcs1003_count" >&2
  echo "stdout/stderr log: $PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG" >&2
  exit 1
fi

echo "InspectCode PackageReference severity override verified."
echo "TCS1001 error x$override_tcs1001_count / TCS1002 error x$override_tcs1002_count / TCS1003 error x$override_tcs1003_count"
echo "stdout/stderr log: $PACKAGE_REFERENCE_OVERRIDE_STDOUT_LOG"

#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LUA_BIN="$SCRIPT_DIR/deps/lua/lua"
BUILD_DIR="$SCRIPT_DIR/build"
TEMP_DIRS=()

cleanup_temp_dirs() {
  if [ "${#TEMP_DIRS[@]}" -gt 0 ]; then
    rm -rf "${TEMP_DIRS[@]}"
  fi
}
trap cleanup_temp_dirs EXIT

build_lua() {
  echo "Building Lua 5.5..."
  cmake -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE=Release -S "$SCRIPT_DIR" >/dev/null 2>&1

  local jobs="${TCS_JOBS:-}"
  if [ -z "$jobs" ]; then
    if command -v nproc >/dev/null 2>&1; then
      jobs="$(nproc)"
    elif command -v sysctl >/dev/null 2>&1; then
      jobs="$(sysctl -n hw.ncpu)"
    else
      jobs="2"
    fi
  fi

  cmake --build "$BUILD_DIR" --parallel "$jobs" >/dev/null 2>&1
  echo "Lua built."
}

needs_lua_build() {
  if [ ! -x "$LUA_BIN" ]; then
    return 0
  fi

  for input in "$SCRIPT_DIR/CMakeLists.txt" \
      "$SCRIPT_DIR/deps/lua/luaconf.h" \
      "$SCRIPT_DIR/deps/lua/lua.c"; do
    if [ "$input" -nt "$LUA_BIN" ]; then
      return 0
    fi
  done

  return 1
}

# Build Lua if missing or stale against the local build inputs
if needs_lua_build; then
  build_lua
fi

LUA_VERSION="$("$LUA_BIN" -v 2>&1)"
if [[ "$LUA_VERSION" != Lua\ 5.5* ]]; then
  echo "Lua binary version mismatch, rebuilding: $LUA_VERSION" >&2
  build_lua
  LUA_VERSION="$("$LUA_BIN" -v 2>&1)"
  if [[ "$LUA_VERSION" != Lua\ 5.5* ]]; then
    echo "Error: expected Lua 5.5, got: $LUA_VERSION" >&2
    exit 1
  fi
fi

# Run dotnet tests (spec conformance sweep と corpus differential 込み。
# 章別レポートも再生成される — 挙動変更は report/baseline の diff として現れる)
echo "Running dotnet tests (with spec conformance + differential)..."
TCS_SPEC_CONFORMANCE=1 \
TCS_SPEC_REPORT="$SCRIPT_DIR/doc/spec-conformance-report.md" \
TCS_DIFFERENTIAL=1 \
  dotnet test "$SCRIPT_DIR" --verbosity quiet

echo "Running tcs check on samples..."
sample_checks=(
  "samples/hello.cs"
  "samples/game.cs"
  "samples/inventory.cs"
  "samples/entity.cs"
  "samples/statemachine.cs"
  "samples/collision.cs"
)
for sample in "${sample_checks[@]}"; do
  dotnet run --project "$SCRIPT_DIR/Transpiler" -- check "$SCRIPT_DIR/$sample"
done
dotnet run --project "$SCRIPT_DIR/Transpiler" -- \
  check "$SCRIPT_DIR/samples/host_api_game.cs" \
  --ref "$SCRIPT_DIR/samples/host_api_stub.cs"

bash "$SCRIPT_DIR/samples/analyzer-demo/verify-rider-scripts.sh"

echo "Running analyzer demo build..."
set +e
analyzer_output="$(dotnet build "$SCRIPT_DIR/samples/analyzer-demo/analyzer-demo.csproj" --no-incremental 2>&1)"
analyzer_exit=$?
set -e
printf '%s\n' "$analyzer_output"
if [ "$analyzer_exit" -ne 0 ]; then
  exit "$analyzer_exit"
fi

count_diagnostic() {
  local id="$1"
  printf '%s\n' "$analyzer_output" \
    | awk -v id="$id" 'index($0, "warning " id ":") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }'
}

assert_contains() {
  local label="$1"
  local output="$2"
  local needle="$3"
  if ! printf '%s\n' "$output" | grep -Fq -- "$needle"; then
    echo "Error: $label did not contain expected diagnostic text: $needle" >&2
    exit 1
  fi
}

assert_expected_diagnostic_texts() {
  local label="$1"
  local output="$2"
  for needle in \
      "StructDeclaration" \
      "LocalFunctionStatement" \
      "TryStatement" \
      "ThrowStatement" \
      "ListPattern" \
      "System.IO.File.ReadAllText" \
      "List<T> cannot store null elements"; do
    assert_contains "$label" "$output" "$needle"
  done
}

tcs1001_count="$(count_diagnostic TCS1001)"
tcs1002_count="$(count_diagnostic TCS1002)"
tcs1003_count="$(count_diagnostic TCS1003)"
if [ "$tcs1001_count" -ne 5 ] || [ "$tcs1002_count" -ne 1 ] || [ "$tcs1003_count" -ne 1 ]; then
  echo "Error: analyzer demo expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count" >&2
  exit 1
fi
assert_expected_diagnostic_texts "analyzer demo" "$analyzer_output"

echo "Running analyzer package consumer build..."
package_dir="$(mktemp -d)"
consumer_dir="$(mktemp -d)"
TEMP_DIRS+=("$package_dir" "$consumer_dir")
dotnet pack "$SCRIPT_DIR/TinyCs.Analyzers/TinyCs.Analyzers.csproj" \
  -c Release \
  -o "$package_dir" >/dev/null
cp "$SCRIPT_DIR/samples/analyzer-demo/Program.cs" "$consumer_dir/Program.cs"
cat > "$consumer_dir/analyzer-package-consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreAdditionalProjectSources>$package_dir</RestoreAdditionalProjectSources>
    <RestorePackagesPath>$consumer_dir/packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TinyCs.Analyzers" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF
cat > "$consumer_dir/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = warning
dotnet_diagnostic.TCS1003.severity = warning
EOF
set +e
consumer_output="$(dotnet build "$consumer_dir/analyzer-package-consumer.csproj" --no-incremental 2>&1)"
consumer_exit=$?
set -e
printf '%s\n' "$consumer_output"
if [ "$consumer_exit" -ne 0 ]; then
  exit "$consumer_exit"
fi
consumer_tcs1001_count="$(printf '%s\n' "$consumer_output" \
  | awk 'index($0, "warning TCS1001:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
consumer_tcs1002_count="$(printf '%s\n' "$consumer_output" \
  | awk 'index($0, "warning TCS1002:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
consumer_tcs1003_count="$(printf '%s\n' "$consumer_output" \
  | awk 'index($0, "warning TCS1003:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
if [ "$consumer_tcs1001_count" -ne 5 ] || [ "$consumer_tcs1002_count" -ne 1 ] || [ "$consumer_tcs1003_count" -ne 1 ]; then
  echo "Error: analyzer package consumer expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$consumer_tcs1001_count / TCS1002 x$consumer_tcs1002_count / TCS1003 x$consumer_tcs1003_count" >&2
  exit 1
fi
assert_expected_diagnostic_texts "analyzer package consumer" "$consumer_output"

echo "Running analyzer package severity override build..."
cat > "$consumer_dir/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = error
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = error
EOF
set +e
override_output="$(dotnet build "$consumer_dir/analyzer-package-consumer.csproj" --no-incremental 2>&1)"
override_exit=$?
set -e
printf '%s\n' "$override_output"
if [ "$override_exit" -eq 0 ]; then
  echo "Error: analyzer package severity override build expected TCS errors" >&2
  exit 1
fi
override_tcs1001_count="$(printf '%s\n' "$override_output" \
  | awk 'index($0, "error TCS1001:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
override_tcs1002_count="$(printf '%s\n' "$override_output" \
  | awk 'index($0, "error TCS1002:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
override_tcs1003_count="$(printf '%s\n' "$override_output" \
  | awk 'index($0, "error TCS1003:") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }')"
if [ "$override_tcs1001_count" -ne 5 ] || [ "$override_tcs1002_count" -ne 1 ] || [ "$override_tcs1003_count" -ne 1 ]; then
  echo "Error: analyzer package severity override expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$override_tcs1001_count / TCS1002 x$override_tcs1002_count / TCS1003 x$override_tcs1003_count" >&2
  exit 1
fi
assert_expected_diagnostic_texts "analyzer package severity override" "$override_output"

echo "All tests passed."

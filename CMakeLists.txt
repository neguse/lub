cmake_minimum_required(VERSION 3.20)
project(lub C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Emscripten branch toggle. LUB_WASM is the canonical flag for everything
# downstream — the rest of this file checks it instead of EMSCRIPTEN directly
# so it's easy to grep all of the wasm-specific divergences.
if(EMSCRIPTEN)
  set(LUB_WASM ON)
endif()

include(FetchContent)

# SDL3 build flags must be set BEFORE FetchContent_MakeAvailable. The
# Emscripten build uses static SDL and disables pthreads/tests so the wasm
# artifact stays self-contained.
if(LUB_WASM)
  set(SDL_PTHREADS OFF CACHE BOOL "" FORCE)
  set(SDL_TESTS    OFF CACHE BOOL "" FORCE)
  set(SDL_SHARED   OFF CACHE BOOL "" FORCE)
  set(SDL_STATIC   ON  CACHE BOOL "" FORCE)
endif()

FetchContent_Declare(
  SDL3
  GIT_REPOSITORY https://github.com/libsdl-org/SDL.git
  GIT_TAG release-3.2.30
)
FetchContent_MakeAvailable(SDL3)

FetchContent_Declare(
  lua
  URL https://www.lua.org/ftp/lua-5.5.0.tar.gz
  URL_HASH SHA256=57ccc32bbbd005cab75bcc52444052535af691789dba2b9016d5c50640d68b3d
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
)
FetchContent_MakeAvailable(lua)

# Lua は素の Makefile なので、ソースを集めて static lib を作る
file(GLOB LUA_SRC CONFIGURE_DEPENDS ${lua_SOURCE_DIR}/src/*.c)
list(REMOVE_ITEM LUA_SRC
  ${lua_SOURCE_DIR}/src/lua.c
  ${lua_SOURCE_DIR}/src/luac.c
)
add_library(lua_static STATIC ${LUA_SRC})
target_include_directories(lua_static PUBLIC ${lua_SOURCE_DIR}/src)

if(NOT LUB_WASM)
  find_package(Vulkan REQUIRED)
endif()

# --- Slang prebuilt -------------------------------------------------------
# We use the Slang prebuilt release (slang.h + slang.lib/libslang.so + DLLs).
# The header tree lives under third_party/slang/include (in-tree). The binary
# pieces (lib/, on Windows also bin/) are gitignored. Auto-fetch them from
# GitHub releases when missing so a fresh checkout builds out of the box.
#
# emscripten: skip the native prebuilt. shader.cpp delegates Slang
# compilation to the browser-side slang-wasm bridge via EM_ASYNC_JS.
if(NOT LUB_WASM)
  set(SLANG_VERSION "2026.8.1")
  set(SLANG_ROOT "${CMAKE_SOURCE_DIR}/third_party/slang")
  if(WIN32)
    set(_slang_lib_marker "${SLANG_ROOT}/lib/slang.lib")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-windows-x86_64.zip")
  elseif(APPLE)
    set(_slang_lib_marker "${SLANG_ROOT}/lib/libslang.dylib")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-macos-aarch64.tar.gz")
  else()
    set(_slang_lib_marker "${SLANG_ROOT}/lib/libslang.so")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-linux-x86_64.tar.gz")
  endif()
  if(NOT EXISTS "${_slang_lib_marker}")
    message(STATUS "Slang prebuilt missing at ${_slang_lib_marker}; downloading ${_slang_archive_url}")
    set(_slang_archive "${CMAKE_BINARY_DIR}/_slang_prebuilt.archive")
    set(_slang_extract "${CMAKE_BINARY_DIR}/_slang_prebuilt")
    file(DOWNLOAD "${_slang_archive_url}" "${_slang_archive}" SHOW_PROGRESS STATUS _slang_dl_status)
    list(GET _slang_dl_status 0 _slang_dl_code)
    if(NOT _slang_dl_code EQUAL 0)
      message(FATAL_ERROR "Failed to download Slang prebuilt: ${_slang_dl_status}")
    endif()
    file(REMOVE_RECURSE "${_slang_extract}")
    file(ARCHIVE_EXTRACT INPUT "${_slang_archive}" DESTINATION "${_slang_extract}")
    file(MAKE_DIRECTORY "${SLANG_ROOT}/lib")
    file(GLOB _slang_libs "${_slang_extract}/lib/*")
    file(COPY ${_slang_libs} DESTINATION "${SLANG_ROOT}/lib")
    if(WIN32)
      file(MAKE_DIRECTORY "${SLANG_ROOT}/bin")
      file(GLOB _slang_bin_items "${_slang_extract}/bin/*")
      file(COPY ${_slang_bin_items} DESTINATION "${SLANG_ROOT}/bin")
    endif()
    if(NOT EXISTS "${_slang_lib_marker}")
      message(FATAL_ERROR "Slang archive did not contain ${_slang_lib_marker}")
    endif()
  endif()
endif()

# Source list. capture.c itself is backend-agnostic (uses g_backend->capture)
# and compiles everywhere; only backend_sdlgpu.c is native-only because the
# SDL_GPU APIs aren't available on the WGPU path we use under wasm. The
# sokol backend's capture stub returns false on wasm.
set(LUB_SOURCES
  src/main.c
  src/app.c
  src/sokol_impl.c
  src/lua_api.c
  src/enums_lua.c
  src/pass.c
  src/resources.c
  src/shader.cpp
  src/pipeline.c
  src/capture.c
  src/backend_sokol.c
  src/stb_impl.c
  src/gltf.c
)
if(NOT LUB_WASM)
  list(APPEND LUB_SOURCES src/backend_sdlgpu.c)
  # haxe --wait child process management; WASM ビルドでは process spawn が無いので除外。
  list(APPEND LUB_SOURCES src/haxe_server.c)
  # hxml parse + haxe --connect 呼び出し + prelude/postlude concat (atomic write)。
  list(APPEND LUB_SOURCES src/haxe_build.c)
endif()
add_executable(lub ${LUB_SOURCES})

target_include_directories(lub PRIVATE
  third_party/sokol
  third_party/slang/include
  third_party/stb
  third_party/cgltf
  src
)
if(NOT LUB_WASM)
  target_link_directories(lub PRIVATE third_party/slang/lib)
endif()

target_link_libraries(lub PRIVATE
  SDL3::SDL3
  lua_static
)
if(NOT LUB_WASM)
  target_link_libraries(lub PRIVATE
    slang
    Vulkan::Vulkan
  )
endif()
if(UNIX AND NOT LUB_WASM)
  # libdl required by Lua dynamic loading, libm by sokol/Lua math (Linux/Mac).
  # Under emscripten the libc already provides both equivalents.
  target_link_libraries(lub PRIVATE m dl)
endif()
if(UNIX AND NOT APPLE AND NOT LUB_WASM)
  # Slang's prebuilt shared libs live next to the source tree; make them
  # discoverable at runtime so the smoke test doesn't need LD_LIBRARY_PATH.
  set_target_properties(lub PROPERTIES
      BUILD_RPATH "${CMAKE_SOURCE_DIR}/third_party/slang/lib"
      INSTALL_RPATH "${CMAKE_SOURCE_DIR}/third_party/slang/lib"
  )
endif()
if(WIN32)
  # On Windows the Slang runtime is a set of DLLs in third_party/slang/bin/.
  # Copy them next to lub.exe so it can resolve them at runtime without
  # putting third_party/slang/bin on PATH.
  add_custom_command(TARGET lub POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
      "${CMAKE_SOURCE_DIR}/third_party/slang/bin"
      "$<TARGET_FILE_DIR:lub>"
    VERBATIM)
  # SDL3 builds as a shared library; copy the import-side DLL too.
  add_custom_command(TARGET lub POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "$<TARGET_FILE:SDL3::SDL3>"
      "$<TARGET_FILE_DIR:lub>"
    VERBATIM)
endif()
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
  target_compile_options(lub PRIVATE
    $<$<COMPILE_LANGUAGE:C>:-Wall -Wextra -Wpedantic>
    $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra>
  )
endif()
if(MSVC)
  # Sources are UTF-8 (Japanese comments); without /utf-8 MSVC will emit
  # C4819 on default code page (CP932 on jp Windows) and may mis-decode.
  target_compile_options(lub PRIVATE /utf-8)
endif()

# Backend define: native uses sokol's Vulkan backend; emscripten uses WebGPU.
if(LUB_WASM)
  target_compile_definitions(lub PRIVATE SOKOL_WGPU)
else()
  target_compile_definitions(lub PRIVATE SOKOL_VULKAN)
endif()

# Emscripten link flags: WebGPU + ASYNCIFY for the (future) Slang fetch, plus
# the samples/lume tree preloaded as virtual FS.
#
# WebGPU is wired up through the `emdawnwebgpu` emscripten port (Dawn's
# Chrome/Node-compatible WGPU header set). The header `webgpu/webgpu.h` is
# referenced from sokol_gfx.h's WGPU backend; the port supplies both the
# header at compile time and the JS glue at link time. -sUSE_WEBGPU=1
# alone is no longer enough on Emscripten >= 3.1.61.
if(LUB_WASM)
  set_target_properties(lub PROPERTIES SUFFIX ".js")
  # --- emdawnwebgpu port ---
  # Provides webgpu/webgpu.h that sokol_gfx's WGPU backend includes. Plain
  # -sUSE_WEBGPU=1 is no longer sufficient on emscripten >= 3.1.61. The port
  # is auto-fetched against the active emsdk's manifest, so the header ABI
  # can shift if the SDK is upgraded.
  #
  # Tested with: emsdk 5.0.2 (emcc 5.0.2 / dc80f645ee70178c11666de0c3860d9e064d50e4).
  # If you upgrade emsdk and sokol_gfx WGPU calls suddenly stop compiling or
  # render-paths regress, revert to this SDK version or update sokol_gfx to a
  # release that tracks the newer Dawn ABI.
  target_compile_options(lub PRIVATE --use-port=emdawnwebgpu)
  # SHELL: keeps multi-token flags (`--preload-file PATH`) as one logical
  # unit so CMake doesn't dedupe / split them. Without SHELL the second
  # `--preload-file` got dropped during CMake's list deduplication and only
  # the first preload path was preserved.
  target_link_options(lub PRIVATE
    --use-port=emdawnwebgpu
    -sASYNCIFY
    -sALLOW_MEMORY_GROWTH=1
    -sASSERTIONS=1
    -sSTACK_SIZE=1048576
    "SHELL:-sEXPORTED_RUNTIME_METHODS=['FS','ccall','UTF8ToString']"
    "SHELL:--preload-file ${CMAKE_SOURCE_DIR}/samples@/samples"
    "SHELL:--preload-file ${CMAKE_SOURCE_DIR}/third_party/lume@/lume"
  )
endif()

# Haxe pipeline C-side smoke tests (native only — spawn は wasm に存在しない).
if(NOT LUB_WASM)
  add_executable(lub_haxe_server_smoke
    tests/c/haxe_server_smoke.c
    src/haxe_server.c
  )
  target_link_libraries(lub_haxe_server_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_haxe_server_smoke PRIVATE src)

  # hxml parser 単体 unit test (haxe 子プロセスは spawn しない)。
  add_executable(lub_hxml_parse_test
    tests/c/hxml_parse_test.c
    src/haxe_build.c
  )
  target_link_libraries(lub_hxml_parse_test PRIVATE SDL3::SDL3)
  target_include_directories(lub_hxml_parse_test PRIVATE src)

  # haxe_build end-to-end smoke (haxe --wait → --connect → atomic write の貫通)。
  add_executable(lub_haxe_build_smoke
    tests/c/haxe_build_smoke.c
    src/haxe_build.c
    src/haxe_server.c
  )
  target_link_libraries(lub_haxe_build_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_haxe_build_smoke PRIVATE src)
endif()

# The lider player (native/main.c): statically links lider's native extensions (kana-kanji conversion, file system operations, the tcs compiler) into the lub player library.
# The lub build is used through find_package(lub) (lub_DIR is the lub build directory). Called by scripts/build-player.sh.
# The web output is build/player-web/web/lub.{js,wasm}.
# Requirements: cargo (for the web, rustup's wasm32-unknown-emscripten), dotnet or the C tcs (to convert tcs to C), GCC or Clang (the tcs C uses GNU C statement expressions; clang-cl on Windows).
cmake_minimum_required(VERSION 3.22)
project(lider_player C CXX)

set(JAMO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/jamo" CACHE PATH "jamo のソース")
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(lub REQUIRED CONFIG)

# Builds jamo-core (Rust) as a static library
# When building for another machine, pass the Rust target with -DJAMO_TARGET= (e.g. aarch64-unknown-linux-gnu)
set(JAMO_TARGET "" CACHE STRING "jamo-core を build する Rust の target (空なら、web は wasm32-unknown-emscripten、ほかは rustc の host)")
if(NOT JAMO_TARGET)
  if(EMSCRIPTEN)
    set(JAMO_TARGET wasm32-unknown-emscripten)
  else()
    execute_process(COMMAND rustc -vV OUTPUT_VARIABLE _rustc_info)
    string(REGEX MATCH "host: ([^\n]+)" _host "${_rustc_info}")
    set(JAMO_TARGET "${CMAKE_MATCH_1}")
  endif()
endif()
# If a prebuilt jamo-core exists, links it (built from the same jamo commit, Rust target and rustc; cargo is not run)
set(JAMO_PREBUILT "" CACHE FILEPATH "build 済みの jamo-core の static library (空なら cargo で build する)")
if(JAMO_PREBUILT)
  set(JAMO_LIB "${JAMO_PREBUILT}")
  add_custom_target(lider_jamo_core)
else()
  set(JAMO_CARGO_DIR "${CMAKE_CURRENT_BINARY_DIR}/jamo")
  if(WIN32)
    set(JAMO_LIB "${JAMO_CARGO_DIR}/${JAMO_TARGET}/release/jamo_core.lib")
  else()
    set(JAMO_LIB "${JAMO_CARGO_DIR}/${JAMO_TARGET}/release/libjamo_core.a")
  endif()
  add_custom_command(OUTPUT "${JAMO_LIB}"
    COMMAND ${CMAKE_COMMAND} -E env CARGO_TARGET_DIR=${JAMO_CARGO_DIR}
      cargo rustc --manifest-path "${JAMO_ROOT}/Cargo.toml" -p jamo-core --release
      --target ${JAMO_TARGET} --crate-type staticlib
    COMMENT "Building jamo-core (${JAMO_TARGET})"
    VERBATIM)
  add_custom_target(lider_jamo_core DEPENDS "${JAMO_LIB}")
endif()

# Converts tcs to C (tcs.c) with src/tcs/to-c.sh. The library shipped with tcs is embedded in a header
set(LUB_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../..")
file(GLOB TCS_SELF_SOURCES CONFIGURE_DEPENDS
  "${LUB_ROOT}/src/tcs/Compiler/*.cs" "${LUB_ROOT}/src/tcs/CBackend/*.cs"
  "${LUB_ROOT}/src/tcs/VmBackend/*.cs" "${LUB_ROOT}/src/tcs/Tool/*.cs")
# The runtime C that `tcs c` embeds in tcs.c
file(GLOB_RECURSE TCS_RUNTIME_SOURCES CONFIGURE_DEPENDS "${LUB_ROOT}/src/tcs-runtime/*.c" "${LUB_ROOT}/src/tcs-runtime/*.h")
set(TCS_C_DIR "${CMAKE_CURRENT_BINARY_DIR}/tcs")
find_program(BASH bash REQUIRED)
# If the C tcs (output of scripts/build-tcs-native.sh) exists, converts to C with it (it emits the same C as the .NET tcs). dotnet is not needed then
set(TCS_NATIVE "" CACHE FILEPATH "C の tcs の実行ファイル (空なら .NET の tcs を使う)")
if(TCS_NATIVE)
  set(TCS_TO_C COMMAND "${CMAKE_COMMAND}" -E env "LUB_TCS=${TCS_NATIVE}" "${BASH}" "${LUB_ROOT}/src/tcs/to-c.sh")
  set(TCS_TO_C_DEPENDS "${TCS_NATIVE}")
else()
  find_program(DOTNET dotnet REQUIRED)
  set(TCS_TO_C COMMAND "${BASH}" "${LUB_ROOT}/src/tcs/to-c.sh")
  set(TCS_TO_C_DEPENDS ${TCS_RUNTIME_SOURCES})
endif()
add_custom_command(OUTPUT "${TCS_C_DIR}/tcs.c"
  ${TCS_TO_C} "${TCS_C_DIR}/tcs.c"
  DEPENDS ${TCS_SELF_SOURCES} "${LUB_ROOT}/src/tcs/sources.sh" "${LUB_ROOT}/src/tcs/to-c.sh" ${TCS_TO_C_DEPENDS}
  COMMENT "Converting tcs to C"
  VERBATIM)
add_custom_command(OUTPUT "${TCS_C_DIR}/tcs_tool_files.h"
  COMMAND "${CMAKE_COMMAND}" -DLUB_ROOT=${LUB_ROOT} -DOUT=${TCS_C_DIR}/tcs_tool_files.h
    -P "${CMAKE_CURRENT_SOURCE_DIR}/tcs/embed.cmake"
  DEPENDS "${LUB_ROOT}/src/tcs/shipped.txt" "${LUB_ROOT}/src/tcs-system/Linq.cs" "${LUB_ROOT}/src/tcs-system/ValueTuple.cs" "${CMAKE_CURRENT_SOURCE_DIR}/tcs/embed.cmake"
  VERBATIM)
# The flags for the C from `tcs c`, the same as src/tcs/cflags.sh. Warnings for the generated C are suppressed
# The generated C uses GNU C statement expressions, so MSVC cl cannot build it. On Windows, build with clang-cl
include(CheckCCompilerFlag)
if(CMAKE_C_COMPILER_ID STREQUAL "MSVC")
  message(FATAL_ERROR "lider の player は MSVC の cl では build できない (tcs c の C が GNU C の文の式を使う)。clang-cl を使う (-DCMAKE_C_COMPILER=clang-cl)")
elseif(CMAKE_C_SIMULATE_ID STREQUAL "MSVC")
  # clang-cl takes GNU-style options through /clang:
  set(TCS_C_OPTIONS "/O2;/clang:-ffp-contract=off;/clang:-fwrapv;/w")
else()
  set(TCS_C_OPTIONS "-O2;-ffp-contract=off;-fwrapv;-w")
  check_c_compiler_flag(-fexcess-precision=standard TCS_C_EXCESS_PRECISION)
  if(TCS_C_EXCESS_PRECISION)
    list(APPEND TCS_C_OPTIONS -fexcess-precision=standard)
  endif()
endif()
set_source_files_properties(tcs/vm_tcs.c PROPERTIES
  OBJECT_DEPENDS "${TCS_C_DIR}/tcs.c;${TCS_C_DIR}/tcs_tool_files.h"
  INCLUDE_DIRECTORIES "${TCS_C_DIR}"
  COMPILE_DEFINITIONS lub_vm_extension=lub_vm_extension_tcs
  COMPILE_OPTIONS "${TCS_C_OPTIONS}")

add_executable(lider_player main.c jamo/vm_jamo.c fs/vm_fs.c tcs/vm_tcs.c)
add_dependencies(lider_player lider_jamo_core)
set_source_files_properties(jamo/vm_jamo.c PROPERTIES
  COMPILE_DEFINITIONS lub_vm_extension=lub_vm_extension_jamo)
set_source_files_properties(fs/vm_fs.c PROPERTIES
  COMPILE_DEFINITIONS lub_vm_extension=lub_vm_extension_fs)
target_include_directories(lider_player PRIVATE "${JAMO_ROOT}/jamo-core/include")
target_link_libraries(lider_player PRIVATE lub::lub_vm_player "${JAMO_LIB}")
if(UNIX AND NOT APPLE AND NOT EMSCRIPTEN)
  target_link_libraries(lider_player PRIVATE pthread dl m)
elseif(WIN32)
  # Windows libraries used by jamo-core (Rust std). WaitOnAddress and others are in synchronization
  target_link_libraries(lider_player PRIVATE ws2_32 userenv ntdll bcrypt synchronization)
endif()
# The web build uses the same settings and names as the lub player (web/lub.{js,wasm,data})
lub_vm_player_link_options(lider_player "${CMAKE_CURRENT_BINARY_DIR}/web")
# The tcs compiler recurses deeply, so the web stack is enlarged
if(EMSCRIPTEN)
  target_link_options(lider_player PRIVATE -sSTACK_SIZE=8388608)
endif()

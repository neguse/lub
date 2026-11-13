# lider の player (native/main.c): lub の player の library に、lider の native の拡張 (かな漢字変換、file system の操作) を静的に link する。
# lub の build は find_package(lub) で使う (lub_DIR に lub の build のディレクトリ)。scripts/build-player.sh が呼ぶ。
# web の出力は build/player-web/web/lub.{js,wasm}。
# 要るもの: cargo (web は rustup の wasm32-unknown-emscripten)。
cmake_minimum_required(VERSION 3.22)
project(lider_player C CXX)

set(JAMO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/jamo" CACHE PATH "jamo のソース")
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(lub REQUIRED CONFIG)

# jamo-core (Rust) を static library にする
if(EMSCRIPTEN)
  set(JAMO_TARGET wasm32-unknown-emscripten)
else()
  execute_process(COMMAND rustc -vV OUTPUT_VARIABLE _rustc_info)
  string(REGEX MATCH "host: ([^\n]+)" _host "${_rustc_info}")
  set(JAMO_TARGET "${CMAKE_MATCH_1}")
endif()
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

add_executable(lider_player main.c jamo/vm_jamo.c fs/vm_fs.c)
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
  target_link_libraries(lider_player PRIVATE ws2_32 userenv ntdll bcrypt)
endif()
# web は lub の player と同じ設定と名前 (web/lub.{js,wasm,data})
lub_vm_player_link_options(lider_player "${CMAKE_CURRENT_BINARY_DIR}/web")

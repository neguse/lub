cmake_minimum_required(VERSION 3.20)
project(sglua C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

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

find_package(Vulkan REQUIRED)

# --- Slang prebuilt -------------------------------------------------------
# We use the Slang prebuilt release (slang.h + slang.lib/libslang.so + DLLs).
# The header tree lives under third_party/slang/include (in-tree). The binary
# pieces (lib/, on Windows also bin/) are gitignored. Auto-fetch them from
# GitHub releases when missing so a fresh checkout builds out of the box.
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

add_executable(sglua
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
  src/backend_sdlgpu.c
  src/stb_impl.c
)
target_include_directories(sglua PRIVATE
  third_party/sokol
  third_party/slang/include
  third_party/stb
  src
)
target_link_directories(sglua PRIVATE third_party/slang/lib)
target_link_libraries(sglua PRIVATE
  SDL3::SDL3
  lua_static
  slang
  Vulkan::Vulkan
)
if(UNIX)
  # libdl required by Lua dynamic loading, libm by sokol/Lua math (Linux/Mac)
  target_link_libraries(sglua PRIVATE m dl)
endif()
if(UNIX AND NOT APPLE)
  # Slang's prebuilt shared libs live next to the source tree; make them
  # discoverable at runtime so the smoke test doesn't need LD_LIBRARY_PATH.
  set_target_properties(sglua PROPERTIES
      BUILD_RPATH "${CMAKE_SOURCE_DIR}/third_party/slang/lib"
      INSTALL_RPATH "${CMAKE_SOURCE_DIR}/third_party/slang/lib"
  )
endif()
if(WIN32)
  # On Windows the Slang runtime is a set of DLLs in third_party/slang/bin/.
  # Copy them next to sglua.exe so it can resolve them at runtime without
  # putting third_party/slang/bin on PATH.
  add_custom_command(TARGET sglua POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
      "${CMAKE_SOURCE_DIR}/third_party/slang/bin"
      "$<TARGET_FILE_DIR:sglua>"
    VERBATIM)
  # SDL3 builds as a shared library; copy the import-side DLL too.
  add_custom_command(TARGET sglua POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "$<TARGET_FILE:SDL3::SDL3>"
      "$<TARGET_FILE_DIR:sglua>"
    VERBATIM)
endif()
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
  target_compile_options(sglua PRIVATE
    $<$<COMPILE_LANGUAGE:C>:-Wall -Wextra -Wpedantic>
    $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra>
  )
endif()
if(MSVC)
  # Sources are UTF-8 (Japanese comments); without /utf-8 MSVC will emit
  # C4819 on default code page (CP932 on jp Windows) and may mis-decode.
  target_compile_options(sglua PRIVATE /utf-8)
endif()
# Vulkan backend を sokol_gfx に指示
target_compile_definitions(sglua PRIVATE
  SOKOL_VULKAN
)

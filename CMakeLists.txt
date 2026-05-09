cmake_minimum_required(VERSION 3.20)
project(sglua C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

include(FetchContent)

FetchContent_Declare(
  SDL3
  GIT_REPOSITORY https://github.com/libsdl-org/SDL.git
  GIT_TAG release-3.2.30
)
FetchContent_MakeAvailable(SDL3)

FetchContent_Declare(
  lua
  URL https://www.lua.org/ftp/lua-5.4.7.tar.gz
  URL_HASH SHA256=9fbf5e28ef86c69858f6d3d34eccc32e911c1a28b4120ff3e84aaa70cfbf1e30
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

add_executable(sglua
  src/main.c
)
target_include_directories(sglua PRIVATE
  third_party/sokol
  third_party/slang/include
  src
)
target_link_directories(sglua PRIVATE third_party/slang/lib)
target_link_libraries(sglua PRIVATE
  SDL3::SDL3
  lua_static
  slang
  # Linux-only PoC; libdl required by Lua dynamic loading, libm by sokol/Lua math
  m dl
)
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
  target_compile_options(sglua PRIVATE -Wall -Wextra -Wpedantic)
endif()
# OpenGL 3.3 backend を sokol_gfx に指示 (Linux)
target_compile_definitions(sglua PRIVATE
  SOKOL_GLCORE
)

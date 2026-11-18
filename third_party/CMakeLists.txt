# lub が link する第三者の library (SDL、Box2D、Box3D、OpenXR の loader) を build して install する project。
# lub の configure (cmake/LubDeps.cmake) が、lub の build のディレクトリの deps/ に install し、lub はそこから find_package で使う。
cmake_minimum_required(VERSION 3.22)
project(lub_deps C CXX)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# lub と同じく、日本語を含む source を MSVC に UTF-8 として読ませる。
if(MSVC)
  add_compile_options(/utf-8)
endif()

# lub の runtime は共有 library (lub_shared) にもなるので、静的に link するものは PIC で作る。
if(NOT EMSCRIPTEN)
  set(CMAKE_POSITION_INDEPENDENT_CODE ON)
endif()

# SDL3。
# web と iOS は静的に link する (web は pthread も使わない)。
set(SDL_INSTALL ON CACHE BOOL "" FORCE)
set(SDL_TESTS OFF CACHE BOOL "" FORCE)
if(EMSCRIPTEN)
  set(SDL_PTHREADS OFF CACHE BOOL "" FORCE)
  set(SDL_SHARED OFF CACHE BOOL "" FORCE)
  set(SDL_STATIC ON CACHE BOOL "" FORCE)
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  set(SDL_SHARED OFF CACHE BOOL "" FORCE)
  set(SDL_STATIC ON CACHE BOOL "" FORCE)
endif()
add_subdirectory(SDL)

# Box2D v3 と Box3D は library だけを作る。
set(BOX2D_SAMPLES OFF CACHE BOOL "" FORCE)
set(BOX2D_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(BOX2D_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(BOX2D_DOCS OFF CACHE BOOL "" FORCE)
set(BOX2D_COMPILE_WARNING_AS_ERROR OFF CACHE BOOL "" FORCE)
add_subdirectory(box2d)
add_subdirectory(box3d)

# OpenXR の loader は Linux と Android の XR (lub の src/xr.c) が使う。
# loader の他 (API layer、test) は作らない。
if(CMAKE_SYSTEM_NAME STREQUAL "Linux" OR ANDROID)
  set(BUILD_WITH_SYSTEM_JSONCPP OFF CACHE BOOL "" FORCE)
  set(BUILD_API_LAYERS OFF CACHE BOOL "" FORCE)
  set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(BUILD_CONFORMANCE_TESTS OFF CACHE BOOL "" FORCE)
  set(BUILD_SDK_TESTS OFF CACHE BOOL "" FORCE)
  # loader は runtime の manifest を <sysconfdir>/openxr/ (と /etc/openxr/) から探し、その path を埋め込む。
  # install の先 (lub の build の中) にせず、既定の prefix と同じ /usr/local/etc を探させる。
  set(CMAKE_INSTALL_SYSCONFDIR /usr/local/etc CACHE PATH "" FORCE)
  add_subdirectory(openxr)
endif()

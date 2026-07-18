cmake_minimum_required(VERSION 3.12)
project(tcs-perf C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

set(PERF_TCS_LUA_SRC
    "/home/neguse/ghq/github.com/neguse/tcs/deps/lua"
    CACHE PATH "Path to the Lua source tree used for the LUA_32BITS embed")
set(PERF_LUA32
    "${PERF_TCS_LUA_SRC}/lua32"
    CACHE FILEPATH "Path to the LUA_32BITS interpreter used by run.sh")

set(PERF_C_FLAGS)
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    list(APPEND PERF_C_FLAGS
        -O2
        -ffp-contract=off
        -fwrapv
        -fexcess-precision=standard)
endif()

set(PERF_LUA_CORE
    ${PERF_TCS_LUA_SRC}/lapi.c
    ${PERF_TCS_LUA_SRC}/lcode.c
    ${PERF_TCS_LUA_SRC}/lctype.c
    ${PERF_TCS_LUA_SRC}/ldebug.c
    ${PERF_TCS_LUA_SRC}/ldo.c
    ${PERF_TCS_LUA_SRC}/ldump.c
    ${PERF_TCS_LUA_SRC}/lfunc.c
    ${PERF_TCS_LUA_SRC}/lgc.c
    ${PERF_TCS_LUA_SRC}/llex.c
    ${PERF_TCS_LUA_SRC}/lmem.c
    ${PERF_TCS_LUA_SRC}/lobject.c
    ${PERF_TCS_LUA_SRC}/lopcodes.c
    ${PERF_TCS_LUA_SRC}/lparser.c
    ${PERF_TCS_LUA_SRC}/lstate.c
    ${PERF_TCS_LUA_SRC}/lstring.c
    ${PERF_TCS_LUA_SRC}/lstrlib.c
    ${PERF_TCS_LUA_SRC}/ltable.c
    ${PERF_TCS_LUA_SRC}/ltablib.c
    ${PERF_TCS_LUA_SRC}/ltm.c
    ${PERF_TCS_LUA_SRC}/lundump.c
    ${PERF_TCS_LUA_SRC}/lvm.c
    ${PERF_TCS_LUA_SRC}/lzio.c)

set(PERF_LUA_LIB
    ${PERF_TCS_LUA_SRC}/lauxlib.c
    ${PERF_TCS_LUA_SRC}/lbaselib.c
    ${PERF_TCS_LUA_SRC}/ldblib.c
    ${PERF_TCS_LUA_SRC}/liolib.c
    ${PERF_TCS_LUA_SRC}/lmathlib.c
    ${PERF_TCS_LUA_SRC}/loslib.c
    ${PERF_TCS_LUA_SRC}/loadlib.c
    ${PERF_TCS_LUA_SRC}/lcorolib.c
    ${PERF_TCS_LUA_SRC}/linit.c
    ${PERF_TCS_LUA_SRC}/lutf8lib.c)

foreach(lua_source IN LISTS PERF_LUA_CORE PERF_LUA_LIB)
    if(NOT EXISTS "${lua_source}")
        message(FATAL_ERROR
            "PERF_TCS_LUA_SRC is not a Lua source tree: missing ${lua_source}")
    endif()
endforeach()

add_library(perf_lua32 STATIC ${PERF_LUA_CORE} ${PERF_LUA_LIB})
target_include_directories(perf_lua32 PUBLIC ${PERF_TCS_LUA_SRC})
target_compile_definitions(perf_lua32 PUBLIC LUA_32BITS)
target_compile_options(perf_lua32 PRIVATE ${PERF_C_FLAGS})

if(WIN32)
    target_compile_definitions(perf_lua32 PRIVATE _CRT_SECURE_NO_WARNINGS)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_compile_definitions(perf_lua32 PRIVATE LUA_USE_LINUX)
    target_link_libraries(perf_lua32 PUBLIC m ${CMAKE_DL_LIBS})
elseif(APPLE)
    target_compile_definitions(perf_lua32 PRIVATE LUA_USE_MACOSX)
    target_link_libraries(perf_lua32 PUBLIC m)
elseif(UNIX)
    target_compile_definitions(perf_lua32 PRIVATE LUA_USE_POSIX)
    target_link_libraries(perf_lua32 PUBLIC m)
else()
    target_compile_definitions(perf_lua32 PRIVATE LUA_USE_C89)
endif()

add_library(perf_common STATIC common.c core.c)
target_include_directories(perf_common PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(perf_common PRIVATE ${PERF_C_FLAGS})

add_executable(native native.c)
target_link_libraries(native PRIVATE perf_common)
target_compile_options(native PRIVATE ${PERF_C_FLAGS})

add_executable(aot-hash aot.c)
target_compile_definitions(aot-hash PRIVATE PERF_AOT_HASH=1)
target_link_libraries(aot-hash PRIVATE perf_common perf_lua32)
target_compile_options(aot-hash PRIVATE ${PERF_C_FLAGS})

add_executable(aot-slot aot.c)
target_compile_definitions(aot-slot PRIVATE PERF_AOT_SLOT=1)
target_link_libraries(aot-slot PRIVATE perf_common perf_lua32)
target_compile_options(aot-slot PRIVATE ${PERF_C_FLAGS})

file(GENERATE
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/lua32-path.txt"
    CONTENT "${PERF_LUA32}\n")

cmake_minimum_required(VERSION 3.12)
project(aot-ceiling-spike C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

set(SPIKE_TCS_LUA_SRC
    "/home/neguse/ghq/github.com/neguse/tcs/deps/lua"
    CACHE PATH "Path to the Lua source tree used for the LUA_32BITS embed")
set(SPIKE_LUA32
    "${SPIKE_TCS_LUA_SRC}/lua32"
    CACHE FILEPATH "Path to the LUA_32BITS interpreter used by run.sh")

set(SPIKE_C_FLAGS)
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    list(APPEND SPIKE_C_FLAGS
        -O2
        -ffp-contract=off
        -fwrapv
        -fexcess-precision=standard)
endif()

set(SPIKE_LUA_CORE
    ${SPIKE_TCS_LUA_SRC}/lapi.c
    ${SPIKE_TCS_LUA_SRC}/lcode.c
    ${SPIKE_TCS_LUA_SRC}/lctype.c
    ${SPIKE_TCS_LUA_SRC}/ldebug.c
    ${SPIKE_TCS_LUA_SRC}/ldo.c
    ${SPIKE_TCS_LUA_SRC}/ldump.c
    ${SPIKE_TCS_LUA_SRC}/lfunc.c
    ${SPIKE_TCS_LUA_SRC}/lgc.c
    ${SPIKE_TCS_LUA_SRC}/llex.c
    ${SPIKE_TCS_LUA_SRC}/lmem.c
    ${SPIKE_TCS_LUA_SRC}/lobject.c
    ${SPIKE_TCS_LUA_SRC}/lopcodes.c
    ${SPIKE_TCS_LUA_SRC}/lparser.c
    ${SPIKE_TCS_LUA_SRC}/lstate.c
    ${SPIKE_TCS_LUA_SRC}/lstring.c
    ${SPIKE_TCS_LUA_SRC}/lstrlib.c
    ${SPIKE_TCS_LUA_SRC}/ltable.c
    ${SPIKE_TCS_LUA_SRC}/ltablib.c
    ${SPIKE_TCS_LUA_SRC}/ltm.c
    ${SPIKE_TCS_LUA_SRC}/lundump.c
    ${SPIKE_TCS_LUA_SRC}/lvm.c
    ${SPIKE_TCS_LUA_SRC}/lzio.c)

set(SPIKE_LUA_LIB
    ${SPIKE_TCS_LUA_SRC}/lauxlib.c
    ${SPIKE_TCS_LUA_SRC}/lbaselib.c
    ${SPIKE_TCS_LUA_SRC}/ldblib.c
    ${SPIKE_TCS_LUA_SRC}/liolib.c
    ${SPIKE_TCS_LUA_SRC}/lmathlib.c
    ${SPIKE_TCS_LUA_SRC}/loslib.c
    ${SPIKE_TCS_LUA_SRC}/loadlib.c
    ${SPIKE_TCS_LUA_SRC}/lcorolib.c
    ${SPIKE_TCS_LUA_SRC}/linit.c
    ${SPIKE_TCS_LUA_SRC}/lutf8lib.c)

foreach(lua_source IN LISTS SPIKE_LUA_CORE SPIKE_LUA_LIB)
    if(NOT EXISTS "${lua_source}")
        message(FATAL_ERROR
            "SPIKE_TCS_LUA_SRC is not a Lua source tree: missing ${lua_source}")
    endif()
endforeach()

add_library(spike_lua32 STATIC ${SPIKE_LUA_CORE} ${SPIKE_LUA_LIB})
target_include_directories(spike_lua32 PUBLIC ${SPIKE_TCS_LUA_SRC})
target_compile_definitions(spike_lua32 PUBLIC LUA_32BITS)
target_compile_options(spike_lua32 PRIVATE ${SPIKE_C_FLAGS})

if(WIN32)
    target_compile_definitions(spike_lua32 PRIVATE _CRT_SECURE_NO_WARNINGS)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_compile_definitions(spike_lua32 PRIVATE LUA_USE_LINUX)
    target_link_libraries(spike_lua32 PUBLIC m ${CMAKE_DL_LIBS})
elseif(APPLE)
    target_compile_definitions(spike_lua32 PRIVATE LUA_USE_MACOSX)
    target_link_libraries(spike_lua32 PUBLIC m)
elseif(UNIX)
    target_compile_definitions(spike_lua32 PRIVATE LUA_USE_POSIX)
    target_link_libraries(spike_lua32 PUBLIC m)
else()
    target_compile_definitions(spike_lua32 PRIVATE LUA_USE_C89)
endif()

add_library(spike_common STATIC common.c)
target_include_directories(spike_common PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(spike_common PRIVATE ${SPIKE_C_FLAGS})

add_executable(native native.c)
target_link_libraries(native PRIVATE spike_common)
target_compile_options(native PRIVATE ${SPIKE_C_FLAGS})

add_executable(aot-hash aot.c)
target_compile_definitions(aot-hash PRIVATE SPIKE_AOT_HASH=1)
target_link_libraries(aot-hash PRIVATE spike_common spike_lua32)
target_compile_options(aot-hash PRIVATE ${SPIKE_C_FLAGS})

add_executable(aot-slot aot.c)
target_compile_definitions(aot-slot PRIVATE SPIKE_AOT_SLOT=1)
target_link_libraries(aot-slot PRIVATE spike_common spike_lua32)
target_compile_options(aot-slot PRIVATE ${SPIKE_C_FLAGS})

file(GENERATE
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/lua32-path.txt"
    CONTENT "${SPIKE_LUA32}\n")

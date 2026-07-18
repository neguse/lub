cmake_minimum_required(VERSION 3.12)
project(tcs-lua C)

set(LUA_SRC ${CMAKE_CURRENT_SOURCE_DIR}/deps/lua)

# Lua core sources
set(LUA_CORE
    ${LUA_SRC}/lapi.c ${LUA_SRC}/lcode.c ${LUA_SRC}/lctype.c
    ${LUA_SRC}/ldebug.c ${LUA_SRC}/ldo.c ${LUA_SRC}/ldump.c
    ${LUA_SRC}/lfunc.c ${LUA_SRC}/lgc.c ${LUA_SRC}/llex.c
    ${LUA_SRC}/lmem.c ${LUA_SRC}/lobject.c ${LUA_SRC}/lopcodes.c
    ${LUA_SRC}/lparser.c ${LUA_SRC}/lstate.c ${LUA_SRC}/lstring.c
    ${LUA_SRC}/lstrlib.c ${LUA_SRC}/ltable.c ${LUA_SRC}/ltablib.c
    ${LUA_SRC}/ltm.c ${LUA_SRC}/lundump.c ${LUA_SRC}/lvm.c
    ${LUA_SRC}/lzio.c
)

# Lua auxiliary + standard libraries
set(LUA_LIB
    ${LUA_SRC}/lauxlib.c ${LUA_SRC}/lbaselib.c ${LUA_SRC}/ldblib.c
    ${LUA_SRC}/liolib.c ${LUA_SRC}/lmathlib.c ${LUA_SRC}/loslib.c
    ${LUA_SRC}/loadlib.c ${LUA_SRC}/lcorolib.c ${LUA_SRC}/linit.c
    ${LUA_SRC}/lutf8lib.c
)

# --- liblua (static library, shared by lua) ---
add_library(liblua STATIC ${LUA_CORE} ${LUA_LIB})
target_include_directories(liblua PUBLIC ${LUA_SRC})

set(TCS_LUA_DEFINITIONS)
set(TCS_LUA_SYSTEM_LIBS)

if(WIN32)
    list(APPEND TCS_LUA_DEFINITIONS _CRT_SECURE_NO_WARNINGS)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    list(APPEND TCS_LUA_DEFINITIONS LUA_USE_LINUX)
    list(APPEND TCS_LUA_SYSTEM_LIBS m ${CMAKE_DL_LIBS})
elseif(APPLE)
    if(CMAKE_SYSTEM_NAME STREQUAL "iOS"
            OR CMAKE_SYSTEM_NAME STREQUAL "tvOS"
            OR CMAKE_SYSTEM_NAME STREQUAL "watchOS")
        list(APPEND TCS_LUA_DEFINITIONS LUA_USE_IOS)
    else()
        list(APPEND TCS_LUA_DEFINITIONS LUA_USE_MACOSX)
    endif()
    list(APPEND TCS_LUA_SYSTEM_LIBS m)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Emscripten")
    # WASM builds should not assume POSIX dlopen/stat/readline support.
    list(APPEND TCS_LUA_DEFINITIONS LUA_USE_C89)
elseif(CMAKE_SYSTEM_NAME MATCHES "^(FreeBSD|OpenBSD|NetBSD|DragonFly)$")
    list(APPEND TCS_LUA_DEFINITIONS LUA_USE_POSIX)
    list(APPEND TCS_LUA_SYSTEM_LIBS m)
elseif(UNIX)
    # Generic Unix: use POSIX libc helpers, but do not assume dlopen needs -ldl.
    list(APPEND TCS_LUA_DEFINITIONS LUA_USE_POSIX)
    list(APPEND TCS_LUA_SYSTEM_LIBS m)
else()
    list(APPEND TCS_LUA_DEFINITIONS LUA_USE_C89)
endif()

target_compile_definitions(liblua PRIVATE ${TCS_LUA_DEFINITIONS})
if(TCS_LUA_SYSTEM_LIBS)
    target_link_libraries(liblua PUBLIC ${TCS_LUA_SYSTEM_LIBS})
endif()

# --- lua (standard interpreter) ---
add_executable(lua ${LUA_SRC}/lua.c)
target_link_libraries(lua liblua)
target_compile_definitions(lua PRIVATE ${TCS_LUA_DEFINITIONS})
set_target_properties(lua PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${LUA_SRC}
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${LUA_SRC}
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${LUA_SRC}
)

# --- lua32: LUA_32BITS 変種 (int32 + float32) ---
# IL の数値モデル (il-design §4 / il-spec §5-6) と性能実測
# (perf/README.md) の実行基盤。
option(TCS_LUA_32BITS_VARIANT "Build lua32 (LUA_32BITS) alongside lua" ON)
if(TCS_LUA_32BITS_VARIANT)
    add_library(liblua32 STATIC ${LUA_CORE} ${LUA_LIB})
    target_include_directories(liblua32 PUBLIC ${LUA_SRC})
    target_compile_definitions(liblua32
        PRIVATE ${TCS_LUA_DEFINITIONS} LUA_32BITS)
    if(TCS_LUA_SYSTEM_LIBS)
        target_link_libraries(liblua32 PUBLIC ${TCS_LUA_SYSTEM_LIBS})
    endif()

    add_executable(lua32 ${LUA_SRC}/lua.c)
    target_link_libraries(lua32 liblua32)
    target_compile_definitions(lua32
        PRIVATE ${TCS_LUA_DEFINITIONS} LUA_32BITS)
    set_target_properties(lua32 PROPERTIES
        OUTPUT_NAME lua32
        RUNTIME_OUTPUT_DIRECTORY ${LUA_SRC}
        RUNTIME_OUTPUT_DIRECTORY_DEBUG ${LUA_SRC}
        RUNTIME_OUTPUT_DIRECTORY_RELEASE ${LUA_SRC}
    )
endif()

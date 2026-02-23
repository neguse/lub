cmake_minimum_required(VERSION 3.10)
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

# Lua interpreter entry point
set(LUA_EXE ${LUA_SRC}/lua.c)

add_executable(lua ${LUA_CORE} ${LUA_LIB} ${LUA_EXE})
target_include_directories(lua PRIVATE ${LUA_SRC})

if(WIN32)
    # Windows: no special platform defines needed for basic Lua
    target_compile_definitions(lua PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
    # Linux/macOS: enable POSIX features
    target_compile_definitions(lua PRIVATE LUA_USE_LINUX)
    target_link_libraries(lua m dl)
endif()

# Output the binary into deps/lua/ so TestHelper can find it
set_target_properties(lua PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${LUA_SRC}
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${LUA_SRC}
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${LUA_SRC}
)

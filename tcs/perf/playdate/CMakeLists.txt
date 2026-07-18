cmake_minimum_required(VERSION 3.19)
set(CMAKE_C_STANDARD 11)

set(ENVSDK $ENV{PLAYDATE_SDK_PATH})

if (NOT ${ENVSDK} STREQUAL "")
	file(TO_CMAKE_PATH ${ENVSDK} SDK)
else()
	execute_process(
			COMMAND bash -c "egrep '^\\s*SDKRoot' $HOME/.Playdate/config"
			COMMAND head -n 1
			COMMAND cut -c9-
			OUTPUT_VARIABLE SDK
			OUTPUT_STRIP_TRAILING_WHITESPACE
	)
endif()

if (NOT EXISTS ${SDK})
	message(FATAL_ERROR "SDK Path not found; set ENV value PLAYDATE_SDK_PATH")
	return()
endif()

set(PLAYDATE_GAME_NAME tcs_perf)
set(PLAYDATE_GAME_DEVICE tcs_perf_DEVICE)

project(${PLAYDATE_GAME_NAME} C ASM)

get_filename_component(PERF_DIR ${CMAKE_CURRENT_SOURCE_DIR}/.. ABSOLUTE)
get_filename_component(PERF_TCS_LUA_SRC ${PERF_DIR}/../deps/lua ABSOLUTE)

# bench.lua はバイト配列ヘッダとして埋め込む (デバイスに fs 前提を持ち込まない)
file(READ ${PERF_DIR}/interp/bench.lua PERF_BENCH_HEX HEX)
string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," PERF_BENCH_BYTES "${PERF_BENCH_HEX}")
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/gen/bench_lua.h
	"static const char bench_lua[] = {${PERF_BENCH_BYTES}};\n")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
	${PERF_DIR}/interp/bench.lua)

set(PERF_LUA_SOURCES
	lapi.c lcode.c lctype.c ldebug.c ldo.c ldump.c lfunc.c lgc.c llex.c
	lmem.c lobject.c lopcodes.c lparser.c lstate.c lstring.c lstrlib.c
	ltable.c ltablib.c ltm.c lundump.c lvm.c lzio.c
	lauxlib.c lbaselib.c ldblib.c liolib.c lmathlib.c loslib.c loadlib.c
	lcorolib.c linit.c lutf8lib.c)
list(TRANSFORM PERF_LUA_SOURCES PREPEND ${PERF_TCS_LUA_SRC}/)

set(PERF_SOURCES
	src/main.c
	src/pd_shim.c
	src/native_impl.c
	src/aot_hash_impl.c
	src/aot_slot_impl.c
	src/interp_runner.c
	${PERF_DIR}/core.c
	${PERF_LUA_SOURCES})

if (TOOLCHAIN STREQUAL "armgcc")
	add_executable(${PLAYDATE_GAME_DEVICE} ${PERF_SOURCES})
	set(PERF_TARGET ${PLAYDATE_GAME_DEVICE})
else()
	add_library(${PLAYDATE_GAME_NAME} SHARED ${PERF_SOURCES})
	set(PERF_TARGET ${PLAYDATE_GAME_NAME})
endif()

include(${SDK}/C_API/buildsupport/playdate_game.cmake)

target_include_directories(${PERF_TARGET} PRIVATE
	${PERF_DIR} ${PERF_TCS_LUA_SRC} ${CMAKE_CURRENT_BINARY_DIR}/gen)
# f32 決定性フラグは perf/CMakeLists.txt (ホスト測定) と揃える
target_compile_definitions(${PERF_TARGET} PRIVATE LUA_32BITS)
target_compile_options(${PERF_TARGET} PRIVATE
	-ffp-contract=off -fwrapv -fexcess-precision=standard
	-Wno-double-promotion)
target_link_libraries(${PERF_TARGET} PRIVATE m)

if (TOOLCHAIN STREQUAL "armgcc")
	# newlib の syscall スタブ (Lua の stdio 経路のリンク用)。
	# malloc 系は SDK の setup.c が pd->system->realloc へ接続する
	target_link_options(${PERF_TARGET} PRIVATE --specs=nosys.specs)
	# newlib setjmp (Lua の ldo.c が使用) が ARM unwinder を引き込むが、
	# SDK の link_map.ld は exidx セクションを置かないため空テーブルを与える。
	# C のみ・例外なしなので unwinder は実行されない
	target_link_options(${PERF_TARGET} PRIVATE
		-Wl,--defsym=__exidx_start=0,--defsym=__exidx_end=0)
endif()

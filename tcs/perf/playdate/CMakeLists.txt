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

set(PERF_SOURCES
	src/main.c
	src/pd_shim.c
	src/native_impl.c
	${PERF_DIR}/core.c)

if (TOOLCHAIN STREQUAL "armgcc")
	add_executable(${PLAYDATE_GAME_DEVICE} ${PERF_SOURCES})
	set(PERF_TARGET ${PLAYDATE_GAME_DEVICE})
else()
	add_library(${PLAYDATE_GAME_NAME} SHARED ${PERF_SOURCES})
	set(PERF_TARGET ${PLAYDATE_GAME_NAME})
endif()

include(${SDK}/C_API/buildsupport/playdate_game.cmake)

target_include_directories(${PERF_TARGET} PRIVATE ${PERF_DIR})
# f32 決定性フラグは perf/CMakeLists.txt (ホスト測定) と揃える
target_compile_options(${PERF_TARGET} PRIVATE
	-ffp-contract=off -fwrapv -fexcess-precision=standard
	-Wno-double-promotion)
target_link_libraries(${PERF_TARGET} PRIVATE m)

if (TOOLCHAIN STREQUAL "armgcc")
	# newlib の syscall スタブ (stdio 経路のリンク用)。
	# malloc 系は SDK の setup.c が pd->system->realloc へ接続する
	target_link_options(${PERF_TARGET} PRIVATE --specs=nosys.specs)
endif()

cmake_minimum_required(VERSION 3.12)
project(tcs-perf C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

set(PERF_C_FLAGS)
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    list(APPEND PERF_C_FLAGS
        -O2
        -ffp-contract=off
        -fwrapv
        -fexcess-precision=standard)
endif()

add_library(perf_common STATIC common.c core.c)
target_include_directories(perf_common PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(perf_common PRIVATE ${PERF_C_FLAGS})

add_executable(native native.c)
target_link_libraries(native PRIVATE perf_common)
target_compile_options(native PRIVATE ${PERF_C_FLAGS})

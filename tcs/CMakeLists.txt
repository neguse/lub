cmake_minimum_required(VERSION 3.12)
project(tcs C)

# --- runtime/math: transcendental functions of the TinyC# Math ---
# tcs2c embeds them into the generated C, and tcsvm links them.
# Built with fused multiply-add and fast-math disabled so that results do not change.
file(GLOB TCS_MATH_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/math/*.c)
add_library(tcs_math STATIC ${TCS_MATH_SRC})
if(MSVC)
    target_compile_options(tcs_math PRIVATE /fp:precise)
else()
    target_compile_options(tcs_math PRIVATE -ffp-contract=off -fno-fast-math)
endif()

# --- tcsvm: VM for the bytecode (.tcsb) built from IL (docs/tcs/vm-design.md) ---
# Built with the same flags as runtime/math so that f32 operations are not fused.
file(GLOB TCSVM_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-vm/*.c)
add_executable(tcsvm
    ${TCSVM_SRC}
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/sort/tcs_sort.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/model/tcs_model.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/model/tcs_walk.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/utf8/tcs_utf8.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/fmt/f2s.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/fmt/printf.c
    ${CMAKE_CURRENT_SOURCE_DIR}/../src/tcs-runtime/fmt/parse.c)
target_link_libraries(tcsvm tcs_math)
set_property(TARGET tcsvm PROPERTY C_STANDARD 11)
if(MSVC)
    target_compile_options(tcsvm PRIVATE /fp:precise)
else()
    target_compile_options(tcsvm PRIVATE -ffp-contract=off -fno-fast-math)
    target_link_libraries(tcsvm m)
endif()
set_target_properties(tcsvm PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/tcsvm
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_CURRENT_BINARY_DIR}/tcsvm
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/tcsvm)

cmake_minimum_required(VERSION 3.12)
project(tcs C)

# --- runtime/math: TinyC# の Math の超越関数 ---
# tcs2c は生成 C に埋め込み、tcsvm は link する。
# 結果が変わらないよう、積和の融合と fast-math を止めて build する。
file(GLOB TCS_MATH_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/runtime/math/*.c)
add_library(tcs_math STATIC ${TCS_MATH_SRC})
if(MSVC)
    target_compile_options(tcs_math PRIVATE /fp:precise)
else()
    target_compile_options(tcs_math PRIVATE -ffp-contract=off -fno-fast-math)
endif()

# --- tcsvm: IL から作った bytecode (.tcsb) の VM (doc/vm-design.md) ---
# f32 の演算を融合させないため、runtime/math と同じ flag で build する。
file(GLOB TCSVM_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/tcsvm/vm/*.c)
add_executable(tcsvm
    ${TCSVM_SRC}
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/sort/tcs_sort.c
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/model/tcs_model.c
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/model/tcs_walk.c
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/utf8/tcs_utf8.c
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/fmt/f2s.c
    ${CMAKE_CURRENT_SOURCE_DIR}/runtime/fmt/printf.c)
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

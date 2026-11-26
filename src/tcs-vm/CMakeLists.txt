# tcs-vm: the VM that runs a .tcsb made by `tcs vm` (docs/tcs/vm-design.md), as a library (tcsvm.h), and tcsvm, the command that runs one.
# The root CMakeLists.txt adds this directory. Configured on its own (cmake -S src/tcs-vm), it builds only the VM, for the tcs tests.
# Built with the same flags as tcs-runtime so that f32 operations are not fused.
cmake_minimum_required(VERSION 3.16)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
  project(tcs-vm C)
  add_subdirectory(../tcs-runtime tcs-runtime)
endif()

file(GLOB TCS_VM_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/*.c)
list(FILTER TCS_VM_SRC EXCLUDE REGEX "tcsvm_main\\.c$")
add_library(tcs-vm STATIC ${TCS_VM_SRC})
set_property(TARGET tcs-vm PROPERTY C_STANDARD 11)
target_include_directories(tcs-vm PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(tcs-vm PUBLIC tcs-runtime)
if(MSVC)
  target_compile_options(tcs-vm PRIVATE /fp:precise)
else()
  target_compile_options(tcs-vm PRIVATE -ffp-contract=off -fno-fast-math)
endif()

# The command is a tool of the tcs tests and benchmarks, so a lub build does not build it unless asked
add_executable(tcsvm EXCLUDE_FROM_ALL tcsvm_main.c)
set_property(TARGET tcsvm PROPERTY C_STANDARD 11)
target_link_libraries(tcsvm PRIVATE tcs-vm)
if(MSVC)
  target_compile_options(tcsvm PRIVATE /fp:precise)
else()
  target_compile_options(tcsvm PRIVATE -ffp-contract=off -fno-fast-math)
endif()

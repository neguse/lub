# tcs-runtime: the C that the tcs VM links (the Math transcendental functions, float formatting and parsing, UTF-8, sort, type descriptors).
# `tcs c` embeds the same files, and core/, into the C it generates, so core/ is not built here.
# Built with fused multiply-add and fast-math disabled so that results do not change.
file(GLOB TCS_RUNTIME_MATH CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/math/*.c)
add_library(tcs-runtime STATIC ${TCS_RUNTIME_MATH}
  fmt/f2s.c
  fmt/printf.c
  fmt/parse.c
  utf8/tcs_utf8.c
  sort/tcs_sort.c
  model/tcs_model.c
  model/tcs_walk.c)
set_property(TARGET tcs-runtime PROPERTY C_STANDARD 11)
if(MSVC)
  target_compile_options(tcs-runtime PRIVATE /fp:precise)
else()
  target_compile_options(tcs-runtime PRIVATE -ffp-contract=off -fno-fast-math)
  target_link_libraries(tcs-runtime PUBLIC m)
endif()

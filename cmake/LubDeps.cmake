# Builds the third-party libraries that lub links (third_party/CMakeLists.txt) during lub's configure and installs them into <build>/deps/.
# The inputs of what was installed (submodule commits and diffs, compiler, toolchain, and deps options) are written to <build>/deps/lub-deps.key.
# If the inputs match the key, nothing is built.
# CI keeps <build>/deps/ in the cache and skips the third_party build as long as the key matches.
# The deps build tree is kept in <build>/deps-build/, and when the inputs change only the difference is rebuilt.
# When a submodule's HEAD moves, the next build reruns lub's configure and rebuilds the deps too.
# When a file inside a submodule is edited by hand, reconfigure lub.

set(LUB_DEPS_PREFIX "${CMAKE_CURRENT_BINARY_DIR}/deps")
set(_lub_deps_build "${CMAKE_CURRENT_BINARY_DIR}/deps-build")
set(_lub_deps_source "${CMAKE_CURRENT_LIST_DIR}/../third_party")
cmake_path(NORMAL_PATH _lub_deps_source)

# Initial cache values passed to the deps configure.
# Copy the toolchain and generator settings, the deps options (SDL_*, BOX2D_*, BOX3D_*, OpenXR's DYNAMIC_LOADER and BUILD_WITH_*), and the Vulkan location from lub's cache.
# lub's options (LUB_*) do not affect the deps, so they are not copied (a LUB_NO_SLANG build also uses the same deps).
set(_lub_deps_init "")
string(APPEND _lub_deps_init "set(CMAKE_INSTALL_LIBDIR lib CACHE PATH \"\" FORCE)\n")
string(APPEND _lub_deps_init "set(CMAKE_C_COMPILER [==[${CMAKE_C_COMPILER}]==] CACHE FILEPATH \"\" FORCE)\n")
string(APPEND _lub_deps_init "set(CMAKE_CXX_COMPILER [==[${CMAKE_CXX_COMPILER}]==] CACHE FILEPATH \"\" FORCE)\n")
get_cmake_property(_lub_deps_cache_vars CACHE_VARIABLES)
list(SORT _lub_deps_cache_vars)
foreach(_var IN LISTS _lub_deps_cache_vars)
  if(_var MATCHES "^(CMAKE_TOOLCHAIN_FILE|CMAKE_CROSSCOMPILING_EMULATOR|CMAKE_MAKE_PROGRAM|CMAKE_BUILD_TYPE|CMAKE_SYSTEM_NAME|CMAKE_SYSTEM_PROCESSOR|CMAKE_SYSTEM_VERSION|CMAKE_OSX_ARCHITECTURES|CMAKE_OSX_DEPLOYMENT_TARGET|CMAKE_OSX_SYSROOT|CMAKE_MSVC_RUNTIME_LIBRARY|ANDROID_ABI|ANDROID_PLATFORM|ANDROID_STL|ANDROID_NDK|Vulkan_LIBRARY|Vulkan_INCLUDE_DIR|DYNAMIC_LOADER)$"
      OR _var MATCHES "^(SDL|BOX2D|BOX3D|BUILD_WITH)_")
    get_property(_type CACHE "${_var}" PROPERTY TYPE)
    if(_type STREQUAL "INTERNAL" OR _type STREQUAL "STATIC")
      continue()
    endif()
    if(_type STREQUAL "UNINITIALIZED")
      set(_type STRING)
    endif()
    string(APPEND _lub_deps_init "set(${_var} [==[$CACHE{${_var}}]==] CACHE ${_type} \"\" FORCE)\n")
  endif()
endforeach()

# key: the initial values above plus the generator, the compiler version, the deps CMake code, and the submodule commits and diffs.
# The install destination path is not included (installed CMake packages are read relative to their own location, so deps/ still works when copied into another build directory).
set(_lub_deps_key "${_lub_deps_init}")
string(APPEND _lub_deps_key "generator=${CMAKE_GENERATOR}|${CMAKE_GENERATOR_PLATFORM}|${CMAKE_GENERATOR_TOOLSET}\n")
string(APPEND _lub_deps_key "c=${CMAKE_C_COMPILER_ID} ${CMAKE_C_COMPILER_VERSION}\n")
string(APPEND _lub_deps_key "cxx=${CMAKE_CXX_COMPILER_ID} ${CMAKE_CXX_COMPILER_VERSION}\n")
file(SHA256 "${_lub_deps_source}/CMakeLists.txt" _hash)
string(APPEND _lub_deps_key "third_party/CMakeLists.txt=${_hash}\n")
file(SHA256 "${CMAKE_CURRENT_LIST_FILE}" _hash)
string(APPEND _lub_deps_key "cmake/LubDeps.cmake=${_hash}\n")
find_package(Git QUIET)
foreach(_sub SDL box2d box3d openxr)
  set(_dir "${_lub_deps_source}/${_sub}")
  if(GIT_FOUND AND EXISTS "${_dir}/.git")
    execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${_dir}" rev-parse HEAD
      OUTPUT_VARIABLE _head OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
    execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${_dir}" diff HEAD
      OUTPUT_VARIABLE _diff COMMAND_ERROR_IS_FATAL ANY)
    string(SHA256 _hash "${_diff}")
    string(APPEND _lub_deps_key "${_sub}=${_head} diff=${_hash}\n")
    execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${_dir}" rev-parse --path-format=absolute --git-path HEAD
      OUTPUT_VARIABLE _head_file OUTPUT_STRIP_TRAILING_WHITESPACE COMMAND_ERROR_IS_FATAL ANY)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_head_file}")
  else()
    string(APPEND _lub_deps_key "${_sub}=no-git\n")
  endif()
endforeach()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_lub_deps_source}/CMakeLists.txt")

set(_lub_deps_key_file "${LUB_DEPS_PREFIX}/lub-deps.key")
set(_lub_deps_installed_key "")
if(EXISTS "${_lub_deps_key_file}")
  file(READ "${_lub_deps_key_file}" _lub_deps_installed_key)
endif()
if(_lub_deps_installed_key STREQUAL _lub_deps_key)
  message(STATUS "lub deps: up to date (${LUB_DEPS_PREFIX})")
else()
  message(STATUS "lub deps: building third_party into ${LUB_DEPS_PREFIX}")
  set(_config "${CMAKE_BUILD_TYPE}")
  if(NOT _config)
    set(_config Release)
  endif()
  set(_generator_args -G "${CMAKE_GENERATOR}")
  if(CMAKE_GENERATOR_PLATFORM)
    list(APPEND _generator_args -A "${CMAKE_GENERATOR_PLATFORM}")
  endif()
  if(CMAKE_GENERATOR_TOOLSET)
    list(APPEND _generator_args -T "${CMAKE_GENERATOR_TOOLSET}")
  endif()
  # So that previous settings do not remain in the cache, configure deletes the cache and starts over (objects remain, so the build is incremental).
  file(MAKE_DIRECTORY "${_lub_deps_build}")
  file(REMOVE "${_lub_deps_build}/CMakeCache.txt")
  file(WRITE "${_lub_deps_build}/lub-deps-init.cmake"
    "set(CMAKE_INSTALL_PREFIX [==[${LUB_DEPS_PREFIX}]==] CACHE PATH \"\" FORCE)\n${_lub_deps_init}")
  execute_process(
    COMMAND "${CMAKE_COMMAND}" ${_generator_args} --no-warn-unused-cli
      -C "${_lub_deps_build}/lub-deps-init.cmake"
      -S "${_lub_deps_source}" -B "${_lub_deps_build}"
    COMMAND_ERROR_IS_FATAL ANY)
  execute_process(
    COMMAND "${CMAKE_COMMAND}" --build "${_lub_deps_build}" --config "${_config}" --parallel
    COMMAND_ERROR_IS_FATAL ANY)
  # So that files from an old version do not remain, the install destination is emptied before installing.
  file(REMOVE_RECURSE "${LUB_DEPS_PREFIX}")
  execute_process(
    COMMAND "${CMAKE_COMMAND}" --install "${_lub_deps_build}" --config "${_config}"
    OUTPUT_QUIET
    COMMAND_ERROR_IS_FATAL ANY)
  file(WRITE "${_lub_deps_key_file}" "${_lub_deps_key}")
endif()

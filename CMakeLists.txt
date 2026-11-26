cmake_minimum_required(VERSION 3.22)
project(lub C CXX)
if(APPLE)
  enable_language(OBJC)
endif()

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Sources are UTF-8 (Japanese comments); without /utf-8 MSVC decodes them with
# the system code page (CP932 on jp Windows) and can mis-parse multibyte
# sequences as line continuations. Applies to every target incl. smoke tests.
if(MSVC)
  add_compile_options(/utf-8)
endif()

# Emscripten branch toggle. LUB_WASM is the canonical flag for everything
# downstream — the rest of this file checks it instead of EMSCRIPTEN directly
# so it's easy to grep all of the wasm-specific divergences.
if(EMSCRIPTEN)
  set(LUB_WASM ON)
endif()

# Compiled dependencies are pinned as git submodules under third_party/ so
# their sources live at stable in-tree paths. Run
#   git submodule update --init
# after cloning. Pinned versions: SDL release-3.4.16, Box2D v3.1.1, Box3D
# v0.1.0 (alpha).
if(NOT EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/third_party/SDL/CMakeLists.txt)
  message(FATAL_ERROR "Submodules are missing. Run: git submodule update --init")
endif()

# The runtime also becomes a shared library (lub_shared), so everything statically linked is built as PIC (tess2 / lub itself).
# Third-party libraries are built with the same setting by third_party/CMakeLists.txt.
if(NOT LUB_WASM)
  set(CMAKE_POSITION_INDEPENDENT_CODE ON)
endif()

# Linux uses Vulkan as its default native backend, so its loader and headers
# remain required. Windows defaults to D3D12 and adds the Vulkan backend only
# when the SDK is available.
# Third-party libraries (the OpenXR loader) are also built with the same Vulkan, so it is searched for before cmake/LubDeps.cmake.
set(LUB_HAS_VULKAN OFF)
if(NOT LUB_WASM)
  if(CMAKE_SYSTEM_NAME STREQUAL "Linux" OR ANDROID)
    # On Android the Vulkan headers and libvulkan.so are in the NDK sysroot.
    find_package(Vulkan REQUIRED)
    set(LUB_HAS_VULKAN ON)
  elseif(WIN32)
    find_package(Vulkan QUIET)
    if(Vulkan_FOUND)
      set(LUB_HAS_VULKAN ON)
    else()
      message(STATUS "Vulkan SDK not found; Windows Vulkan backend is disabled")
    endif()
  endif()
endif()

# Third-party libraries (SDL3, Box2D, Box3D, OpenXR loader) are built into <build>/deps/ by third_party/CMakeLists.txt and used from there.
include(cmake/LubDeps.cmake)
find_package(SDL3 REQUIRED CONFIG PATHS "${LUB_DEPS_PREFIX}" NO_DEFAULT_PATH NO_CMAKE_FIND_ROOT_PATH)
find_package(box2d REQUIRED CONFIG PATHS "${LUB_DEPS_PREFIX}" NO_DEFAULT_PATH NO_CMAKE_FIND_ROOT_PATH)
find_package(box3d REQUIRED CONFIG PATHS "${LUB_DEPS_PREFIX}" NO_DEFAULT_PATH NO_CMAKE_FIND_ROOT_PATH)

# The C that the tcs VM links (src/tcs-runtime/CMakeLists.txt)
add_subdirectory(src/tcs-runtime)

# libtess2 (glyph outline tessellation for font_glyph_mesh)
add_library(tess2 STATIC
  third_party/libtess2/Source/bucketalloc.c
  third_party/libtess2/Source/dict.c
  third_party/libtess2/Source/geom.c
  third_party/libtess2/Source/mesh.c
  third_party/libtess2/Source/priorityq.c
  third_party/libtess2/Source/sweep.c
  third_party/libtess2/Source/tess.c
)
target_include_directories(tess2 PUBLIC third_party/libtess2/Include)

# --- Slang prebuilt -------------------------------------------------------
# We use the Slang prebuilt release (slang.h + slang.lib/libslang.so + DLLs).
# The header tree lives under third_party/slang/include (in-tree). The binary
# pieces (lib/, on Windows also bin/) are gitignored. Auto-fetch them from
# GitHub releases when missing so a fresh checkout builds out of the box.
#
# emscripten: skip the native prebuilt. shader.cpp delegates Slang
# compilation to the browser-side slang-wasm bridge via EM_JS.
#
# LUB_NO_SLANG builds a player without the compiler. It can only use shaders
# from the shader cache (src/shader_cache.h), which a player with Slang
# fills. iOS and Android have no Slang prebuilt, so they are always built
# this way.
set(SLANG_VERSION "2026.8.1")
# The prebuilt lib/ (and bin/ on Windows) is fetched for the target architecture.
# A cross build points this at its own tree so it does not mix with the host's libraries.
set(SLANG_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/slang" CACHE PATH "Slang prebuilt lib/ and bin/")
option(LUB_NO_SLANG "Build a player without the Slang shader compiler" OFF)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS" OR ANDROID)
  set(LUB_NO_SLANG ON CACHE BOOL "" FORCE)
endif()
set(LUB_HAS_SLANG OFF)
if(NOT LUB_WASM AND NOT LUB_NO_SLANG)
  set(LUB_HAS_SLANG ON)
endif()
# The Windows prebuilts (Slang, DXC, Agility SDK, WARP) are split per CPU. DXC and the NuGet packages are placed in bin/<LUB_WIN_ARCH>.
if(WIN32)
  if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(ARM64|arm64|aarch64)$")
    set(LUB_WIN_ARCH arm64)
    set(_slang_win_arch aarch64)
  else()
    set(LUB_WIN_ARCH x64)
    set(_slang_win_arch x86_64)
  endif()
endif()
if(LUB_HAS_SLANG)
  if(WIN32)
    set(_slang_lib_marker "${SLANG_ROOT}/lib/slang.lib")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-windows-${_slang_win_arch}.zip")
  elseif(APPLE)
    set(_slang_lib_marker "${SLANG_ROOT}/lib/libslang.dylib")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-macos-aarch64.tar.gz")
  elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux" AND CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(_slang_lib_marker "${SLANG_ROOT}/lib/libslang.so")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-linux-aarch64.tar.gz")
  else()
    set(_slang_lib_marker "${SLANG_ROOT}/lib/libslang.so")
    set(_slang_archive_url
      "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-linux-x86_64.tar.gz")
  endif()
  if(NOT EXISTS "${_slang_lib_marker}")
    message(STATUS "Slang prebuilt missing at ${_slang_lib_marker}; downloading ${_slang_archive_url}")
    set(_slang_archive "${CMAKE_CURRENT_BINARY_DIR}/_slang_prebuilt.archive")
    set(_slang_extract "${CMAKE_CURRENT_BINARY_DIR}/_slang_prebuilt")
    file(DOWNLOAD "${_slang_archive_url}" "${_slang_archive}" SHOW_PROGRESS STATUS _slang_dl_status)
    list(GET _slang_dl_status 0 _slang_dl_code)
    if(NOT _slang_dl_code EQUAL 0)
      message(FATAL_ERROR "Failed to download Slang prebuilt: ${_slang_dl_status}")
    endif()
    file(REMOVE_RECURSE "${_slang_extract}")
    file(ARCHIVE_EXTRACT INPUT "${_slang_archive}" DESTINATION "${_slang_extract}")
    file(MAKE_DIRECTORY "${SLANG_ROOT}/lib")
    file(GLOB _slang_libs "${_slang_extract}/lib/*")
    file(COPY ${_slang_libs} DESTINATION "${SLANG_ROOT}/lib")
    if(WIN32)
      file(MAKE_DIRECTORY "${SLANG_ROOT}/bin")
      file(GLOB _slang_bin_items "${_slang_extract}/bin/*")
      file(COPY ${_slang_bin_items} DESTINATION "${SLANG_ROOT}/bin")
    endif()
    if(NOT EXISTS "${_slang_lib_marker}")
      message(FATAL_ERROR "Slang archive did not contain ${_slang_lib_marker}")
    endif()
  endif()

  # --- DXC (dxcompiler.dll) -------------------------------------------------
  # The d3d12 backend compiles Slang -> DXIL, and Slang delegates DXIL emission
  # to dxcompiler.dll, which the Slang prebuilt does not bundle. Fetch it from
  # the DirectXShaderCompiler release into ${SLANG_ROOT}/bin, which is
  # already copied next to lub.exe post-build. DXC >= 1.8.2502 signs DXIL with
  # the open-source validator hash, so dxil.dll is not needed.
  # License: University of Illinois/NCSA (LLVM Release License).
  if(WIN32)
    set(_dxc_marker "${SLANG_ROOT}/bin/dxcompiler.dll")
    # DXC license bundled for distribution (copied next to lub.exe as dxc-LICENSE-*.txt)
    set(_dxc_license_marker "${SLANG_ROOT}/bin/dxc-LICENSE-MS.txt")
    set(_dxc_archive_url
      "https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2602.24/dxc_2026_05_27.zip")
    if(NOT EXISTS "${_dxc_marker}" OR NOT EXISTS "${_dxc_license_marker}")
      message(STATUS "dxcompiler.dll or its license missing; downloading ${_dxc_archive_url}")
      set(_dxc_archive "${CMAKE_CURRENT_BINARY_DIR}/_dxc_prebuilt.archive")
      set(_dxc_extract "${CMAKE_CURRENT_BINARY_DIR}/_dxc_prebuilt")
      file(DOWNLOAD "${_dxc_archive_url}" "${_dxc_archive}" SHOW_PROGRESS STATUS _dxc_dl_status)
      list(GET _dxc_dl_status 0 _dxc_dl_code)
      if(NOT _dxc_dl_code EQUAL 0)
        message(FATAL_ERROR "Failed to download DXC prebuilt: ${_dxc_dl_status}")
      endif()
      file(REMOVE_RECURSE "${_dxc_extract}")
      file(ARCHIVE_EXTRACT INPUT "${_dxc_archive}" DESTINATION "${_dxc_extract}")
      file(COPY "${_dxc_extract}/bin/${LUB_WIN_ARCH}/dxcompiler.dll" DESTINATION "${SLANG_ROOT}/bin")
      foreach(_dxc_license LLVM MIT MS)
        file(COPY_FILE "${_dxc_extract}/LICENSE-${_dxc_license}.txt" "${SLANG_ROOT}/bin/dxc-LICENSE-${_dxc_license}.txt")
      endforeach()
      if(NOT EXISTS "${_dxc_marker}")
        message(FATAL_ERROR "DXC archive did not contain bin/${LUB_WIN_ARCH}/dxcompiler.dll")
      endif()
    endif()
  endif()

  # --- DirectX 12 Agility SDK -----------------------------------------------
  # The d3d12 backend runs on the Agility SDK runtime instead of the inbox
  # D3D12: D3D12Core.dll ships next to lub.exe under D3D12/ and the exe
  # exports D3D12SDKVersion / D3D12SDKPath (backend_d3d12.cpp). Headers come
  # from the same NuGet package so D3D12_SDK_VERSION matches the DLL. The
  # package is redistributable (its LICENSE.txt covers build/native/bin).
  if(WIN32)
    set(LUB_AGILITY_VERSION "1.619.5")
    set(LUB_AGILITY_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/d3d12agility")
    set(_agility_marker "${LUB_AGILITY_ROOT}/build/native/bin/${LUB_WIN_ARCH}/D3D12Core.dll")
    if(NOT EXISTS "${_agility_marker}")
      set(_agility_url
        "https://www.nuget.org/api/v2/package/Microsoft.Direct3D.D3D12/${LUB_AGILITY_VERSION}")
      message(STATUS "Agility SDK missing; downloading ${_agility_url}")
      set(_agility_archive "${CMAKE_CURRENT_BINARY_DIR}/_agility_sdk.zip")
      file(DOWNLOAD "${_agility_url}" "${_agility_archive}" SHOW_PROGRESS STATUS _agility_dl_status)
      list(GET _agility_dl_status 0 _agility_dl_code)
      if(NOT _agility_dl_code EQUAL 0)
        message(FATAL_ERROR "Failed to download the Agility SDK: ${_agility_dl_status}")
      endif()
      file(REMOVE_RECURSE "${LUB_AGILITY_ROOT}")
      file(ARCHIVE_EXTRACT INPUT "${_agility_archive}" DESTINATION "${LUB_AGILITY_ROOT}")
      if(NOT EXISTS "${_agility_marker}")
        message(FATAL_ERROR "Agility SDK package did not contain build/native/bin/${LUB_WIN_ARCH}/D3D12Core.dll")
      endif()
    endif()
    # WARP from its own NuGet package for software-rasterizer runs (CI golden):
    # the runtime uses a d3d10warp.dll placed next to the exe.
    option(LUB_FETCH_WARP "Stage the NuGet WARP (d3d10warp.dll) next to lub.exe" OFF)
    if(LUB_FETCH_WARP)
      set(LUB_WARP_VERSION "1.0.20")
      set(LUB_WARP_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/d3d12warp")
      set(_warp_marker "${LUB_WARP_ROOT}/build/native/bin/${LUB_WIN_ARCH}/d3d10warp.dll")
      if(NOT EXISTS "${_warp_marker}")
        set(_warp_url
          "https://www.nuget.org/api/v2/package/Microsoft.Direct3D.WARP/${LUB_WARP_VERSION}")
        message(STATUS "WARP missing; downloading ${_warp_url}")
        set(_warp_archive "${CMAKE_CURRENT_BINARY_DIR}/_warp.zip")
        file(DOWNLOAD "${_warp_url}" "${_warp_archive}" SHOW_PROGRESS STATUS _warp_dl_status)
        list(GET _warp_dl_status 0 _warp_dl_code)
        if(NOT _warp_dl_code EQUAL 0)
          message(FATAL_ERROR "Failed to download WARP: ${_warp_dl_status}")
        endif()
        file(REMOVE_RECURSE "${LUB_WARP_ROOT}")
        file(ARCHIVE_EXTRACT INPUT "${_warp_archive}" DESTINATION "${LUB_WARP_ROOT}")
        if(NOT EXISTS "${_warp_marker}")
          message(FATAL_ERROR "WARP package did not contain build/native/bin/${LUB_WIN_ARCH}/d3d10warp.dll")
        endif()
      endif()
    endif()
  endif()
endif()

# Source list. capture.c itself is backend-agnostic (uses g_backend->capture)
# and compiles everywhere. backend_sdlgpu.c / backend_d3d12.cpp are
# native-only; wasm builds ship the webgpu backend alone.
set(LUB_SOURCES
  src/app.c
  src/api_xr.c
  src/host_api.c
  src/api_core.c
  src/api_gfx.c
  src/api_bindings.c
  src/api_sys.c
  src/api_audio.c
  src/api_app.c
  src/api_io.c
  src/api_font.c
  src/api_mesh.c
  src/lubx/vorbis.c
  src/pass.c
  src/pick.c
  src/profile.c
  src/gpu_stats.c
  src/gpu_time.c
  src/resources.c
  src/physics_box2d.c
  src/physics_box3d.c
  src/shader.cpp
  src/pipeline.c
  src/capture.c
  src/digest.c
  src/stb_impl.c
  src/audio.c
  src/miniaudio_impl.c
  src/gltf.c
  src/host.c
  src/surfacenets.c
  src/sdf.c
  src/font.c
  src/ui.cpp
  third_party/imgui/imgui.cpp
  third_party/imgui/imgui_draw.cpp
  third_party/imgui/imgui_tables.cpp
  third_party/imgui/imgui_widgets.cpp
)
list(APPEND LUB_SOURCES src/backend_webgpu.c)
# Generated shader cache (required data for players without slang)
list(APPEND LUB_SOURCES src/shader_cache.c)
list(APPEND LUB_SOURCES src/path_util.c)
if(NOT LUB_WASM)
  list(APPEND LUB_SOURCES src/backend_sdlgpu.c)
  # Export backend (`lub export`, does not use the GPU)
  list(APPEND LUB_SOURCES src/backend_export.c src/export_web.c)
  # tcs (TinyC#) child process: tcs vm --watch frames and tcs check (native only)
  list(APPEND LUB_SOURCES src/tcs_build.c)
  if(WIN32)
    # D3D12 backend ("d3d12", Windows default).
    list(APPEND LUB_SOURCES src/backend_d3d12.cpp)
  endif()
  if(LUB_HAS_VULKAN)
    # Vulkan backend ("vulkan", Linux default; on Windows only when the SDK is detected).
    list(APPEND LUB_SOURCES src/backend_vulkan.c)
  endif()
  if(APPLE)
    # Metal backend ("metal", macOS / iOS default).
    list(APPEND LUB_SOURCES src/backend_metal.m)
    set_source_files_properties(src/backend_metal.m PROPERTIES
      COMPILE_OPTIONS "-fobjc-arc")
  endif()
endif()
if((CMAKE_SYSTEM_NAME STREQUAL "Linux" OR ANDROID) AND LUB_HAS_VULKAN)
  find_package(OpenXR REQUIRED CONFIG PATHS "${LUB_DEPS_PREFIX}" NO_DEFAULT_PATH NO_CMAKE_FIND_ROOT_PATH)
  list(APPEND LUB_SOURCES src/xr.c)
  set(LUB_HAS_OPENXR ON)
endif()
if(ANDROID)
  # Copy the APK data to internal storage (src/android.c)
  list(APPEND LUB_SOURCES src/android.c)
endif()
# The runtime itself is gathered into an object library, which both the player (lub) and the shared library (lub_shared, used by the .NET execution host via P/Invoke) link.
if(LUB_WASM)
  list(APPEND LUB_SOURCES src/webxr.c)
endif()
add_library(lub_objs OBJECT ${LUB_SOURCES})
if(LUB_HAS_OPENXR)
  target_compile_definitions(lub_objs PUBLIC LUB_HAS_OPENXR=1)
  target_link_libraries(lub_objs PUBLIC OpenXR::openxr_loader)
endif()
set(LUB_TCS_GAME "" CACHE FILEPATH "tcs2c game source")
set(LUB_TCS_BINDING "" CACHE FILEPATH "lub-gen tcs binding")
set(LUB_TCS_HOST "${CMAKE_CURRENT_SOURCE_DIR}/src/tcs_host.c" CACHE FILEPATH "tcs2c host entry point")
if(LUB_TCS_GAME)
  if(NOT LUB_TCS_BINDING)
    message(FATAL_ERROR "LUB_TCS_BINDING is required with LUB_TCS_GAME")
  endif()
  if(ANDROID)
    # libmain.so loaded by SDLActivity (scripts/build-android.sh)
    add_library(lub SHARED "${LUB_TCS_HOST}")
    set_target_properties(lub PROPERTIES OUTPUT_NAME main)
  else()
    add_executable(lub "${LUB_TCS_HOST}")
  endif()
  target_compile_definitions(lub PRIVATE
    LUB_TCS_GAME="${LUB_TCS_GAME}" LUB_TCS_BINDING="${LUB_TCS_BINDING}")
  target_compile_options(lub PRIVATE -ffp-contract=off -fwrapv -fexcess-precision=standard)
elseif(ANDROID)
  message(FATAL_ERROR "The Android player runs a tcs2c game: set LUB_TCS_GAME and LUB_TCS_BINDING")
elseif(NOT LUB_WASM)
  add_executable(lub src/main.c)
endif()
if(TARGET lub)
  target_link_libraries(lub PRIVATE lub_objs)
endif()

# tcs VM player (src/vm_player.c): runs a .tcsb made by tcs vm on the tcs VM.
# The VM is src/tcs-vm/CMakeLists.txt.
add_subdirectory(src/tcs-vm)
include(cmake/LubVmPlayer.cmake)
# library: custom players (their own main with statically linked native extensions) link the same one (include/lub/lub_vm.h)
add_library(lub_vm_player STATIC src/vm_player.c)
if(NOT LUB_WASM)
  target_sources(lub_vm_player PRIVATE src/vm_project.c)
endif()
target_link_libraries(lub_vm_player PUBLIC lub_objs tcs-vm)
target_include_directories(lub_vm_player PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
if(LUB_WASM)
  # Web tcs VM player (src/vm_main.c)
  add_executable(lub_vm src/vm_main.c)
  target_link_libraries(lub_vm PRIVATE lub_vm_player)
elseif(NOT LUB_TCS_GAME)
  # Native lub runs games with the tcs VM player (src/main.c)
  target_link_libraries(lub PRIVATE lub_vm_player)
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  # The iOS player is an app bundle.
  # .tcsb, data, and cache/shader/ are placed inside the bundle (scripts/apple-gate.sh).
  # Info.plist is written with CMake variables, so both the Xcode and Ninja generators produce the same bundle.
  set_target_properties(lub PROPERTIES
    MACOSX_BUNDLE TRUE
    MACOSX_BUNDLE_INFO_PLIST "${CMAKE_CURRENT_SOURCE_DIR}/src/ios/Info.plist"
    MACOSX_BUNDLE_GUI_IDENTIFIER "dev.neguse.lub"
    MACOSX_BUNDLE_BUNDLE_NAME "lub"
    XCODE_ATTRIBUTE_PRODUCT_BUNDLE_IDENTIFIER "dev.neguse.lub")
endif()
if(LUB_HAS_VULKAN)
  target_compile_definitions(lub_objs PUBLIC LUB_HAS_VULKAN=1)
endif()
if(NOT LUB_WASM AND NOT ANDROID)
  add_library(lub_shared SHARED)
  target_link_libraries(lub_shared PRIVATE lub_objs)
  set_target_properties(lub_shared PROPERTIES OUTPUT_NAME lub)
  if(WIN32)
    # Make LUB_API in include/lub/lub_api.h dllexport
    target_compile_definitions(lub_objs PUBLIC LUB_BUILD_SHARED)
  endif()
endif()

target_include_directories(lub_objs PUBLIC
  include
  third_party/slang/include
  third_party/stb
  third_party/cgltf
  third_party/miniaudio
  third_party/imgui
  src
)
# The backend index buffer is fixed to u32, so make ImDrawIdx 32-bit.
target_compile_definitions(lub_objs PUBLIC ImDrawIdx=unsigned)
if(LUB_HAS_SLANG)
  target_link_directories(lub_objs PUBLIC "${SLANG_ROOT}/lib")
endif()
# Generator version folded into the shader cache key (the same value for players without Slang)
target_compile_definitions(lub_objs PRIVATE
  LUB_SLANG_VERSION="${SLANG_VERSION}")
if(LUB_NO_SLANG)
  target_compile_definitions(lub_objs PUBLIC LUB_NO_SLANG)
endif()

target_link_libraries(lub_objs PUBLIC
  SDL3::SDL3
  box2d::box2d
  box3d::box3d
  tess2
)
if(LUB_HAS_SLANG)
  target_link_libraries(lub_objs PUBLIC slang)
endif()
if(UNIX AND NOT LUB_WASM)
  # libm for the C math functions (Linux/Mac).
  # Under emscripten the libc already provides it.
  target_link_libraries(lub_objs PUBLIC m)
endif()
if(WIN32)
  # d3d12/dxgi/dxguid for the d3d12 backend (all OS-provided).
  target_link_libraries(lub_objs PUBLIC d3d12 dxgi dxguid)
endif()
if(LUB_HAS_VULKAN)
  target_link_libraries(lub_objs PUBLIC Vulkan::Vulkan)
endif()
if(APPLE)
  target_link_libraries(lub_objs PUBLIC
    "-framework Metal" "-framework QuartzCore" "-framework Foundation")
  if(CMAKE_GENERATOR STREQUAL "Xcode")
    # The Xcode generator links a target whose own sources are all C
    # (main.c) without the C++ runtime that lub_objs needs.
    target_link_libraries(lub_objs PUBLIC c++)
  endif()
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  # miniaudio drives AVAudioSession on iOS, which needs Objective-C.
  set_source_files_properties(src/miniaudio_impl.c PROPERTIES LANGUAGE OBJC)
  target_link_libraries(lub_objs PUBLIC
    "-framework AVFoundation" "-framework AudioToolbox" "-framework CoreAudio")
endif()
if(UNIX AND NOT APPLE AND NOT LUB_WASM AND NOT ANDROID)
  # Slang's prebuilt shared libs live next to the source tree; make them
  # discoverable at runtime so the smoke test doesn't need LD_LIBRARY_PATH.
  set_target_properties(lub lub_shared PROPERTIES
      BUILD_RPATH "${SLANG_ROOT}/lib"
      INSTALL_RPATH "${SLANG_ROOT}/lib"
  )
endif()
if(WIN32)
  # lub.exe links the same objects as lub.dll, whose C API is dllexport, so
  # MSVC also writes an import library for the exe. Keep it apart from
  # lub.dll's lub.lib / lub.exp: both targets link into the same directory,
  # possibly at the same time, and a shared name is a build race
  # (LNK1104 cannot open file 'lub.exp').
  set_target_properties(lub PROPERTIES ENABLE_EXPORTS ON ARCHIVE_OUTPUT_NAME lub_exe)
  # Agility SDK headers ahead of the Windows SDK ones, so D3D12_SDK_VERSION is
  # the shipped runtime's.
  target_include_directories(lub_objs BEFORE PUBLIC "${LUB_AGILITY_ROOT}/build/native/include")
  # On Windows the Slang runtime is a set of DLLs in ${SLANG_ROOT}/bin/.
  # Copy them next to lub.exe so it can resolve them at runtime without
  # putting ${SLANG_ROOT}/bin on PATH.
  add_custom_command(TARGET lub POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
      "${SLANG_ROOT}/bin"
      "$<TARGET_FILE_DIR:lub>"
    VERBATIM)
  # Agility SDK runtime under D3D12/ (where the D3D12SDKPath export points).
  # d3d12SDKLayers.dll is its debug layer; it stays out of shipped builds.
  add_custom_command(TARGET lub POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:lub>/D3D12"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${LUB_AGILITY_ROOT}/build/native/bin/${LUB_WIN_ARCH}/D3D12Core.dll"
      "${LUB_AGILITY_ROOT}/build/native/bin/${LUB_WIN_ARCH}/d3d12SDKLayers.dll"
      "$<TARGET_FILE_DIR:lub>/D3D12"
    VERBATIM)
  if(LUB_FETCH_WARP)
    add_custom_command(TARGET lub POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${LUB_WARP_ROOT}/build/native/bin/${LUB_WIN_ARCH}/d3d10warp.dll"
        "$<TARGET_FILE_DIR:lub>"
      VERBATIM)
  endif()
  # SDL3 builds as a shared library; copy the import-side DLL too.
  add_custom_command(TARGET lub POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "$<TARGET_FILE:SDL3::SDL3>"
      "$<TARGET_FILE_DIR:lub>"
    VERBATIM)
endif()
if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
  foreach(t lub_objs lub)
    if(TARGET ${t})
      target_compile_options(${t} PRIVATE
        $<$<COMPILE_LANGUAGE:C>:-Wall -Wextra -Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra>
      )
    endif()
  endforeach()
  # Warnings in miniaudio itself cannot be fixed on our side, so suppress them only in the implementation TU.
  set_source_files_properties(src/miniaudio_impl.c PROPERTIES COMPILE_OPTIONS "-w")
endif()


# Emscripten: WebGPU. No ASYNCIFY: nothing waits inside a frame on web (shader
# compile calls slang-wasm synchronously, GPU results are waited for at the
# frame boundary).
#
# WebGPU is wired up through the `emdawnwebgpu` emscripten port (Dawn's
# Chrome/Node-compatible WGPU header set). The header `webgpu/webgpu.h` is
# referenced from backend_webgpu.c; the port supplies both the
# header at compile time and the JS glue at link time (lub_vm_player_link_options).
# -sUSE_WEBGPU=1 alone is no longer enough on Emscripten >= 3.1.61.
if(LUB_WASM)
  # The port is auto-fetched against the active emsdk's manifest, so the
  # header ABI can shift if the SDK is upgraded.
  #
  # Tested with: emsdk 5.0.2 (emcc 5.0.2 / dc80f645ee70178c11666de0c3860d9e064d50e4).
  # If you upgrade emsdk and wgpu* calls suddenly stop compiling or
  # render-paths regress, revert to this SDK version or update backend_webgpu.c
  # to track the newer Dawn ABI.
  target_compile_options(lub_objs PUBLIC --use-port=emdawnwebgpu)
  # The tcs VM player is vm/lub.{js,wasm} (read by the web site's player page and by export)
  lub_vm_player_link_options(lub_vm "${CMAKE_CURRENT_BINARY_DIR}/vm")
  # The tcs2c web player (scripts/web-tcs-host-build.sh) is lub.{js,wasm} in the build dir
  if(TARGET lub)
    lub_vm_player_link_options(lub "${CMAKE_CURRENT_BINARY_DIR}")
  endif()
endif()

# C-side smoke tests (native only — spawn does not exist in wasm).
if(LUB_HAS_OPENXR)
  add_executable(lub_xr_math_smoke tests/c/xr_math_smoke.c)
  target_link_libraries(lub_xr_math_smoke PRIVATE lub_objs)
  target_include_directories(lub_xr_math_smoke PRIVATE third_party/openxr/src/common)
endif()
if(NOT LUB_WASM)
  add_executable(lub_tcs_pipeline_smoke
    tests/c/tcs_pipeline_smoke.c
    src/tcs_build.c
    src/path_util.c
  )
  target_link_libraries(lub_tcs_pipeline_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_tcs_pipeline_smoke PRIVATE src)

  add_executable(lub_tcs_sources_smoke
    tests/c/tcs_sources_smoke.c
    src/tcs_build.c
    src/path_util.c
  )
  target_link_libraries(lub_tcs_sources_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_tcs_sources_smoke PRIVATE src)

  add_executable(lub_tcs_resolve_smoke
    tests/c/tcs_resolve_smoke.c
    src/tcs_build.c
    src/path_util.c
  )
  target_link_libraries(lub_tcs_resolve_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_tcs_resolve_smoke PRIVATE src)

  # The test includes vm_project.c (to call its static functions directly)
  add_executable(lub_vm_project_smoke
    tests/c/vm_project_smoke.c
    src/tcs_build.c
    src/path_util.c
  )
  target_link_libraries(lub_vm_project_smoke PRIVATE SDL3::SDL3)
  target_include_directories(lub_vm_project_smoke PRIVATE src)

  add_executable(lub_physics_box2d_smoke
    tests/c/physics_box2d_smoke.c
    src/physics_box2d.c
    src/api_core.c
    src/digest.c
  )
  target_link_libraries(lub_physics_box2d_smoke PRIVATE
    SDL3::SDL3
    box2d::box2d
  )
  if(UNIX AND NOT APPLE)
    target_link_libraries(lub_physics_box2d_smoke PRIVATE m)
  endif()
  target_include_directories(lub_physics_box2d_smoke PRIVATE src include)

  add_executable(lub_physics_box3d_smoke
    tests/c/physics_box3d_smoke.c
    src/physics_box3d.c
    src/api_core.c
    src/digest.c
  )
  target_link_libraries(lub_physics_box3d_smoke PRIVATE
    SDL3::SDL3
    box3d::box3d
  )
  if(UNIX AND NOT APPLE)
    target_link_libraries(lub_physics_box3d_smoke PRIVATE m)
  endif()
  target_include_directories(lub_physics_box3d_smoke PRIVATE src include)

  add_executable(lub_surfacenets_smoke
    tests/c/surfacenets_smoke.c
    src/surfacenets.c
  )
  if(UNIX AND NOT APPLE)
    target_link_libraries(lub_surfacenets_smoke PRIVATE m)
  endif()
  target_include_directories(lub_surfacenets_smoke PRIVATE src include)

  add_executable(lub_sdf_smoke
    tests/c/sdf_smoke.c
    src/sdf.c
    src/surfacenets.c
  )
  if(UNIX AND NOT APPLE)
    target_link_libraries(lub_sdf_smoke PRIVATE m)
  endif()
  target_include_directories(lub_sdf_smoke PRIVATE src include)

  add_executable(lub_resources_smoke
    tests/c/resources_smoke.c
    src/resources.c
  )
  target_include_directories(lub_resources_smoke PRIVATE src include)

  add_executable(lub_gamepad_smoke tests/c/gamepad_smoke.c)
  target_link_libraries(lub_gamepad_smoke PRIVATE lub_objs)

  add_executable(lub_window_input_smoke tests/c/window_input_smoke.c)
  target_link_libraries(lub_window_input_smoke PRIVATE lub_objs)

endif()

if(LUB_HAS_SLANG)
  # Buffer layout smoke: StructuredBuffer element structs must pack the same
  # on every target (shader.cpp rejects the rest). Compiles through the
  # SPIR-V (SDL_GPU) target on every platform.
  add_executable(lub_shader_layout_smoke
    tests/c/shader_layout_smoke.c
    src/shader.cpp
    src/shader_cache.c
  )
  target_include_directories(lub_shader_layout_smoke PRIVATE
    src
    third_party/slang/include
  )
  target_link_directories(lub_shader_layout_smoke PRIVATE "${SLANG_ROOT}/lib")
  target_link_libraries(lub_shader_layout_smoke PRIVATE slang SDL3::SDL3)
  if(UNIX AND NOT APPLE)
    set_target_properties(lub_shader_layout_smoke PROPERTIES
      BUILD_RPATH "${SLANG_ROOT}/lib")
  endif()
  if(WIN32)
    add_dependencies(lub_shader_layout_smoke lub) # slang DLLs land via lub's post-build copy
  endif()

  # Slang -> DXIL compile smoke (Windows only; needs dxcompiler.dll from the
  # DXC fetch above, resolved from the build dir where the slang DLLs are
  # copied post-build of lub).
  if(WIN32)
    add_executable(lub_shader_d3d12_smoke
      tests/c/shader_d3d12_smoke.c
      src/shader.cpp
      src/shader_cache.c
    )
    target_include_directories(lub_shader_d3d12_smoke PRIVATE
      src
      third_party/slang/include
    )
    target_link_directories(lub_shader_d3d12_smoke PRIVATE "${SLANG_ROOT}/lib")
    target_link_libraries(lub_shader_d3d12_smoke PRIVATE slang SDL3::SDL3)
    add_dependencies(lub_shader_d3d12_smoke lub) # slang DLLs land via lub's post-build copy
  endif()
endif()

# Write out the build tree targets so that outside projects can use the player library (lub::lub_vm_player) via find_package(lub).
# Users pass this build directory as lub_DIR (example: tests/cmake/player).
# lubConfig.cmake searches for the third-party libraries in <build>/deps/.
set(LUB_EXPORT_TARGETS lub_vm_player lub_objs tcs-vm tcs-runtime tess2)
export(TARGETS ${LUB_EXPORT_TARGETS} NAMESPACE lub:: FILE "${CMAKE_CURRENT_BINARY_DIR}/lubTargets.cmake")
configure_file(cmake/lubConfig.cmake.in "${CMAKE_CURRENT_BINARY_DIR}/lubConfig.cmake" @ONLY)

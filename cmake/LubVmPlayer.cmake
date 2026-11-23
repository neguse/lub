# Settings for the web player executable (used by the web lub_vm, the tcs2c web lub, and custom players that use find_package(lub)).
# Output is <dir>/lub.{js,wasm}.
function(lub_vm_player_link_options target dir)
  # Also called from outside projects, so check EMSCRIPTEN rather than lub's variable (LUB_WASM)
  if(NOT EMSCRIPTEN)
    return()
  endif()
  set_target_properties(${target} PROPERTIES SUFFIX ".js" OUTPUT_NAME lub
    RUNTIME_OUTPUT_DIRECTORY "${dir}")
  # SHELL: groups a multi-token flag into one so that CMake does not split it or drop it as a duplicate.
  target_link_options(${target} PRIVATE
    --use-port=emdawnwebgpu
    # Saves under save/<name>/ (src/api_io.c) are synced to IndexedDB
    -lidbfs.js
    -sALLOW_MEMORY_GROWTH=1
    -sASSERTIONS=1
    -sSTACK_SIZE=1048576
    # ENV: player.ts (dev/test) injects env vars in preRun (copied from ?env=).
    # addRunDependency / removeRunDependency: make main() wait until player.ts has finished placing the fetched files
    "SHELL:-sEXPORTED_RUNTIME_METHODS=['FS','ccall','UTF8ToString','ENV','addRunDependency','removeRunDependency']"
  )
endfunction()

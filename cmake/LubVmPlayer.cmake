# web の player の実行物の設定 (web の lub_vm、tcs2c の web の lub、find_package(lub) した自前の player が使う)。
# 出力は <dir>/lub.{js,wasm}。
function(lub_vm_player_link_options target dir)
  # 外の project からも呼ぶので、lub の変数 (LUB_WASM) でなく EMSCRIPTEN で見る
  if(NOT EMSCRIPTEN)
    return()
  endif()
  set_target_properties(${target} PROPERTIES SUFFIX ".js" OUTPUT_NAME lub
    RUNTIME_OUTPUT_DIRECTORY "${dir}")
  # SHELL: は複数の token の flag を 1 つにまとめ、CMake が分けたり重複として落としたりしないようにする。
  target_link_options(${target} PRIVATE
    --use-port=emdawnwebgpu
    # save/<名前>/ の保存 (src/api_io.c) が IndexedDB に同期する
    -lidbfs.js
    -sALLOW_MEMORY_GROWTH=1
    -sASSERTIONS=1
    -sSTACK_SIZE=1048576
    # ENV: player.ts (dev/test) が preRun で env var を注入する (?env= 転写)。
    # addRunDependency / removeRunDependency: player.ts が取ってきた file を置き終わるまで main() を待たせる
    "SHELL:-sEXPORTED_RUNTIME_METHODS=['FS','ccall','UTF8ToString','ENV','addRunDependency','removeRunDependency']"
  )
endfunction()

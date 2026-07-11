-- lub host API shim (--prelude 用)
-- lub の C runtime は API を flat global (begin_pass, config, ...) として
-- expose する。Haxe pipeline では lub 側の HAXE_PRELUDE が namespace table を
-- 組み立てるが、.lua entry では通らないため、tcs 出力にはこの shim を前置して
-- lub_stub.cs の型と同じ形の global table を用意する。
-- 対応 API は samples/lub/lub_stub.cs と対で増やす。
Lub = {
  config = config,
  quit = quit,
}
Gfx = {
  begin_pass = begin_pass,
  end_pass = end_pass,
  main_tex = main_tex,
  use_shader = use_shader,
  use_buffer = use_buffer,
  draw = draw,
  VERTEX = VERTEX,
  NONE = NONE,
  ALPHA = ALPHA,
}
Input = {
  key_down = key_down,
  key_pressed = key_pressed,
}
-- Io は C 提供ではなく lub 同梱の Lua module (samples/lub_io.lua)
Io = require("lub_io")

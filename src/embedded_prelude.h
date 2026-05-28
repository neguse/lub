#ifndef LUB_EMBEDDED_PRELUDE_H
#define LUB_EMBEDDED_PRELUDE_H

// Haxe -lua の出力に prepend する shim。
// (1) lub runtime は Lua 5.5 (utf8 built-in) だが、Haxe lua target が
//     require("lua-utf8") を出す前提なので alias を貼っておく。
// (2) Haxe extern (lub.Lub / lub.Gfx / lub.Input / lub.Sys) は
//     class-qualified call (e.g. lub.Gfx.begin_pass) を emit するが、
//     lub C 側は global function (begin_pass) として expose しているので、
//     globals を namespace table に集約してギャップを埋める。
//     lub.Io は @:luaRequire("lub_io") 経由で別経路なので shim 不要。
// (3) Haxe lua target は Int の bitwise (^, &, |, <<, >>) を _hx_bit (= bit32)
//     経由で呼ぶ。Lua 5.5 は native bitwise operator を持つが bit32 module
//     を持たないため、Lua 5.5 native bitwise op で bit32 互換 table を
//     preload しておく。
// (4) Haxe Math.atan2 は math.atan2 を emit するが、Lua 5.3+ で削除された
//     (math.atan(y, x) に統合)。Lua 5.5 用に atan2 を補完する。
static const char HAXE_PRELUDE[] =
    "package.preload[\"lua-utf8\"] = function()\n"
    "  return {\n"
    "    len = string.len, char = string.char,\n"
    "    upper = string.upper, lower = string.lower,\n"
    "    find = string.find, sub = string.sub, byte = string.byte,\n"
    "  }\n"
    "end\n"
    "package.preload[\"bit32\"] = function()\n"
    "  local M = {}\n"
    "  local MASK = 0xFFFFFFFF\n"
    "  function M.band(a, b, ...) local r = a & b; for i=1,select('#',...) do r = r & (select(i,...)) end; return r & MASK end\n"
    "  function M.bor(a, b, ...)  local r = a | b; for i=1,select('#',...) do r = r | (select(i,...)) end; return r & MASK end\n"
    "  function M.bxor(a, b, ...) local r = a ~ b; for i=1,select('#',...) do r = r ~ (select(i,...)) end; return r & MASK end\n"
    "  function M.bnot(a) return (~a) & MASK end\n"
    "  function M.lshift(a, n) return (a << n) & MASK end\n"
    "  function M.rshift(a, n) return (a & MASK) >> n end\n"
    "  function M.arshift(a, n) local s = a & MASK; if s >= 0x80000000 then s = s - 0x100000000 end; return (s >> n) & MASK end\n"
    "  return M\n"
    "end\n"
    "if math.atan2 == nil then math.atan2 = function(y, x) return math.atan(y, x) end end\n"
    "lub = lub or {}\n"
    "lub.Lub = { config = config, quit = quit }\n"
    "lub.Gfx = {\n"
    "  begin_pass = begin_pass, end_pass = end_pass,\n"
    "  use_shader = use_shader, use_shader_compute = use_shader_compute,\n"
    "  use_buffer = use_buffer, use_texture = use_texture,\n"
    "  draw = draw, dispatch = dispatch, capture = capture,\n"
    "  main_tex = main_tex,\n"
    "  VERTEX = VERTEX, INDEX = INDEX, UNIFORM = UNIFORM, STORAGE = STORAGE,\n"
    "  RGBA8 = RGBA8, R8 = R8, RG8 = RG8, RGBA16F = RGBA16F, RGBA32F = RGBA32F,\n"
    "  DEPTH16 = DEPTH16, DEPTH24_STENCIL8 = DEPTH24_STENCIL8, DEPTH32F = DEPTH32F,\n"
    "  CLEAR = CLEAR, LOAD = LOAD, DONTCARE = DONTCARE, STORE = STORE,\n"
    "  NONE = NONE, ALPHA = ALPHA, ADDITIVE = ADDITIVE, MULTIPLY = MULTIPLY,\n"
    "  BACK = BACK, FRONT = FRONT,\n"
    "  TRIANGLES = TRIANGLES, TRIANGLE_STRIP = TRIANGLE_STRIP,\n"
    "  LINES = LINES, LINE_STRIP = LINE_STRIP, POINTS = POINTS,\n"
    "  LINEAR = LINEAR, NEAREST = NEAREST, REPEAT = REPEAT, CLAMP = CLAMP,\n"
    "}\n"
    "lub.Input = { key_down = key_down }\n"
    "lub.Sys = { file_mtime = file_mtime, fnv1a64 = fnv1a64,\n"
    "  load_png = load_png, load_gltf = load_gltf }\n";

#endif

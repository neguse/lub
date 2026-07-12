#ifndef LUB_EMBEDDED_PRELUDE_H
#define LUB_EMBEDDED_PRELUDE_H

// Haxe -lua の出力に prepend する shim。Haxe lua target 固有の互換層のみを
// 持つ。lub API の namespace table (Gfx / Input / ...) は言語非依存の
// samples/lub_prelude.lua が boot.lua 経由で注入する。
// (1) lub runtime は Lua 5.5 (utf8 built-in) だが、Haxe lua target が
//     require("lua-utf8") を出す前提なので alias を貼っておく。
// (2) Haxe lua target は Int の bitwise (^, &, |, <<, >>) を _hx_bit (= bit32)
//     経由で呼ぶ。Lua 5.5 は native bitwise operator を持つが bit32 module
//     を持たないため、Lua 5.5 native bitwise op で bit32 互換 table を
//     preload しておく。
// (3) Haxe Math.atan2 / Math.pow は math.atan2 / math.pow を emit するが、
//     どちらも Lua 5.3+ で削除された (atan(y, x) と ^ 演算子に統合)。
//     Lua 5.5 用に両方補完する。
// NOTE: web/scripts/gen-haxe-assets.mjs は HAXE_PRELUDE の宣言以降にある
//       C 文字列リテラル全部を prelude として抽出する。このファイルへ別の
//       リテラルを足すときは宣言より前に置くこと。
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
    "  function M.band(a, b, ...) local r = a & b; for i=1,select('#',...) do "
    "r = r & (select(i,...)) end; return r & MASK end\n"
    "  function M.bor(a, b, ...)  local r = a | b; for i=1,select('#',...) do "
    "r = r | (select(i,...)) end; return r & MASK end\n"
    "  function M.bxor(a, b, ...) local r = a ~ b; for i=1,select('#',...) do "
    "r = r ~ (select(i,...)) end; return r & MASK end\n"
    "  function M.bnot(a) return (~a) & MASK end\n"
    "  function M.lshift(a, n) return (a << n) & MASK end\n"
    "  function M.rshift(a, n) return (a & MASK) >> n end\n"
    "  function M.arshift(a, n) local s = a & MASK; if s >= 0x80000000 then s "
    "= s - 0x100000000 end; return (s >> n) & MASK end\n"
    "  return M\n"
    "end\n"
    "if math.atan2 == nil then math.atan2 = function(y, x) return math.atan(y, "
    "x) end end\n"
    "if math.pow == nil then math.pow = function(a, b) return a ^ b end end\n";

#endif

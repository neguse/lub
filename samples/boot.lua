-- samples/boot.lua
-- root は lub root の絶対パス (cwd が lub root のときは空文字列)。
-- .lua 直パス entry を lub root 以外の cwd から動かすときに C 側が渡す。
local entry_name, root = ...

local p = (root and #root > 0) and (root .. "/") or ""
package.path = "/lume/?.lua;"
	.. p
	.. "third_party/lume/?.lua;"
	.. p
	.. "samples/?.lua;"
	.. p
	.. "tests/lua/?.lua;"
	.. package.path

local ok, lume = pcall(require, "lume")
if not ok then
	error("boot.lua: failed to load lume: " .. tostring(lume))
end
_G.lume = lume

-- 公式 API 面 (namespace table) を entry より先に注入する。
-- 全 authoring 言語が同じ面を見る (samples/lub_prelude.lua 参照)。
require("lub_prelude")

local mod = require(entry_name)
if type(mod) ~= "table" then
	error("boot.lua: module " .. tostring(entry_name) .. " did not return a table")
end
return mod

-- samples/boot.lua
local entry_name = ...

package.path = "/lume/?.lua;third_party/lume/?.lua;samples/?.lua;" .. package.path

local ok, lume = pcall(require, "lume")
if not ok then
    error("boot.lua: failed to load lume: " .. tostring(lume))
end
_G.lume = lume

local mod = require(entry_name)
if type(mod) ~= "table" then
    error("boot.lua: module " .. tostring(entry_name) .. " did not return a table")
end
return mod

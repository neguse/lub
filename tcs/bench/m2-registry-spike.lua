-- M2 スパイク: registry + read-only module _ENV のプロトタイプで
-- 「実際の emitter 出力」を declare/define/initialize に分けて動かす。
-- 検証: (S1) function 文が stable table へ書かれる / (S2) 再 apply で
-- identity 維持 + method 差し替えが既存 instance に効く / (S3) static は
-- hot apply で保持・fresh では初期化 / (S4) env の解決順と write 禁止 /
-- (S5) base relink / (S6) owned key 削除
-- 実行: deps/lua/lua bench/m2-registry-spike.lua
local host_env = {
  Math = { Sqrt = math.sqrt },  -- TinySystem 相当
  print = print, setmetatable = setmetatable, error = error, tostring = tostring,
}

-- ---- registry プロトタイプ (M2 ModuleRegistry の最小形) ----
local function new_registry()
  local types = {}
  local reg = {}
  function reg.declare(id)
    local t = types[id]
    if not t then t = {}; types[id] = t end
    return t
  end
  function reg.types() return types end
  -- module 専用 read-only _ENV: 宣言 type alias → host global の順で解決。
  -- 未宣言 global への write は error。
  function reg.env(aliases)
    return setmetatable({}, {
      __index = function(_, k)
        local id = aliases[k]
        if id then return types[id] end
        return host_env[k]
      end,
      __newindex = function(_, k)
        error("write to undeclared global: " .. tostring(k), 2)
      end,
    })
  end
  return reg
end

-- ---- 実 emitter 出力 (samples/00_hello/.lub/Hello00.lua から逐語コピー) ----
-- M2 の emitter 変更点 = 「Name = {} / static 初期化を define から外す」だけ、
-- という仮説どおり、define chunk には宣言行を含めない。
local define_v1 = [[
Vec2.__index = Vec2

function Vec2.new(x, y)
  local self = setmetatable({}, Vec2)
  self.x = 0
  self.y = 0
  self.x = x
  self.y = y
  return self
end

function Vec2.zero()
  return Vec2.new(0, 0)
end

function Vec2:lengthSq()
  return self.x * self.x + self.y * self.y
end

function Vec2:length()
  return Math.Sqrt(self:lengthSq())
end

function Vec2:normalize()
  local len = self:length()
  return (function() if len > 0 then return Vec2.new(self.x / len, self.y / len) else return Vec2.zero() end end)()
end
]]

-- v2: length の body だけ変更 (2倍を返す)。emitted 形式は同一。
local define_v2 = define_v1:gsub(
  "return Math%.Sqrt%(self:lengthSq%(%)%)",
  "return Math.Sqrt(self:lengthSq()) * 2")

-- 継承 + static を持つ emitted 風 class (現行 emitter の出力形)
local define_derived = [[
Counter.__index = Counter
setmetatable(Counter, {__index = Vec2})

function Counter.bump()
  Counter.count = Counter.count + 1
  return Counter.count
end

function Counter.gone()
  return "will be deleted"
end
]]
local init_counter = [[
Counter.count = 100
]]

local function apply(reg, aliases, chunk)
  local f = assert(load(chunk, "@define", "t", reg.env(aliases)))
  f()
end

-- ---- S1/S2: identity + method swap ----
local reg = new_registry()
local aliases = { Vec2 = "Vec2.cs#Vec2", Counter = "Counter.cs#Counter" }
local V1 = reg.declare("Vec2.cs#Vec2")
apply(reg, aliases, define_v1)
local p = reg.types()["Vec2.cs#Vec2"].new(3, 4)
assert(p:length() == 5, "v1 length")
apply(reg, aliases, define_v2)  -- hot apply: declare は同 table を返す
assert(reg.declare("Vec2.cs#Vec2") == V1, "S2 type table identity")
assert(getmetatable(p) == V1, "S2 instance metatable identity")
assert(p:length() == 10, "S2 existing instance sees new method")
print("S1/S2 ok: function 文は stable table へ、既存 instance に新 method が効く")

-- ---- S3: static (initialize 分離) ----
reg.declare("Counter.cs#Counter")
apply(reg, aliases, define_derived)
apply(reg, aliases, init_counter)          -- fresh 相当: initializer 実行
local c1 = reg.types()["Counter.cs#Counter"].bump()
assert(c1 == 101, "S3 static init + bump")
apply(reg, aliases, define_derived)        -- hot apply: initializer は実行しない
assert(reg.types()["Counter.cs#Counter"].count == 101, "S3 static preserved on hot apply")
print("S3 ok: hot apply で static 保持、initializer 分離が成立")

-- ---- S4: env 解決順 + write 禁止 ----
assert(p.x == 3 and select(2, pcall(function()
  local f = load("Rogue = 1", "@rogue", "t", reg.env(aliases)); f()
end)):match("undeclared global"), "S4 write forbidden")
print("S4 ok: Math.* は host fallback で解決、未宣言 global write は error")

-- ---- S5: base relink (emitted の setmetatable 形そのまま) ----
local mt = getmetatable(reg.types()["Counter.cs#Counter"])
assert(mt and mt.__index == V1, "S5 base link")
assert(reg.types()["Counter.cs#Counter"].zero, "S5 inherited static reachable")
print("S5 ok: setmetatable(D, {__index = B}) が env 経由で stable base に繋がる")

-- ---- S6: owned key 削除 (v2 に無い method を落とす) ----
local define_derived_v2, n = define_derived:gsub(
  "function Counter%.gone.-\nend\n", "")
assert(n == 1, "S6 test setup: gone method removed from v2")
-- ownership 差分 (v1 keys - v2 keys) の削除は registry の仕事
reg.types()["Counter.cs#Counter"].gone = nil
apply(reg, aliases, define_derived_v2)
assert(reg.types()["Counter.cs#Counter"].gone == nil, "S6 deleted key stays gone")
print("S6 ok: owned key 削除が反映される")

print("M2 spike: ALL PASS")

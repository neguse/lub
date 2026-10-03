-- TinyC# Runtime Library for Lua 5.5

local TinySystem = {}

-- List<T> operations
local List = {}
TinySystem.List = List

function List.new(init)
  return init or {}
end

function List.Add(list, item)
  table.insert(list, item)
end

-- eq は struct 要素の値等価 (型別 op_Equality)。省略時は raw ==
function List.Remove(list, item, eq)
  for i = 1, #list do
    if (eq and eq(list[i], item)) or (not eq and list[i] == item) then
      table.remove(list, i)
      return true
    end
  end
  return false
end

function List.RemoveAt(list, index)
  table.remove(list, index + 1) -- 0-indexed to 1-indexed
end

function List.Count(list, predicate)
  if predicate then
    local n = 0
    for i = 1, #list do
      if predicate(list[i]) then n = n + 1 end
    end
    return n
  end
  return #list
end

function List.Contains(list, item, eq)
  for i = 1, #list do
    if (eq and eq(list[i], item)) or (not eq and list[i] == item) then return true end
  end
  return false
end

function List.IndexOf(list, item, eq)
  for i = 1, #list do
    if (eq and eq(list[i], item)) or (not eq and list[i] == item) then return i - 1 end -- return 0-indexed
  end
  return -1
end

function List.Sort(list, comparison)
  if comparison then
    table.sort(list, function(a, b) return comparison(a, b) < 0 end)
  else
    table.sort(list)
  end
end

-- LINQ-style methods
function List.Where(list, predicate)
  local result = {}
  for i = 1, #list do
    if predicate(list[i]) then
      result[#result + 1] = list[i]
    end
  end
  return result
end

function List.Select(list, selector)
  local result = {}
  for i = 1, #list do
    result[i] = selector(list[i])
  end
  return result
end

function List.Any(list, predicate)
  if not predicate then return #list > 0 end
  for i = 1, #list do
    if predicate(list[i]) then return true end
  end
  return false
end

function List.All(list, predicate)
  for i = 1, #list do
    if not predicate(list[i]) then return false end
  end
  return true
end

function List.First(list, predicate)
  if not predicate then
    if #list == 0 then error("Sequence contains no elements") end
    return list[1]
  end
  for i = 1, #list do
    if predicate(list[i]) then return list[i] end
  end
  error("Sequence contains no matching element")
end

-- default は要素型別の C# default(T) (int=0 / bool=false / ref=nil)。
-- transpiler が呼び出しサイトの型から埋め込む。
function List.FirstOrDefault(list, predicate, default)
  if not predicate then
    if #list == 0 then return default end
    return list[1]
  end
  for i = 1, #list do
    if predicate(list[i]) then return list[i] end
  end
  return default
end

function List.OrderBy(list, keySelector)
  local copy = {}
  for i = 1, #list do copy[i] = list[i] end
  table.sort(copy, function(a, b)
    return keySelector(a) < keySelector(b)
  end)
  return copy
end

function List.OrderByDescending(list, keySelector)
  local copy = {}
  for i = 1, #list do copy[i] = list[i] end
  table.sort(copy, function(a, b)
    return keySelector(a) > keySelector(b)
  end)
  return copy
end

function List.Take(list, count)
  local result = {}
  if count < 0 then count = 0 end
  local limit = math.min(count, #list)
  for i = 1, limit do result[#result + 1] = list[i] end
  return result
end

function List.Skip(list, count)
  local result = {}
  if count < 0 then count = 0 end
  for i = count + 1, #list do result[#result + 1] = list[i] end
  return result
end

function List.Last(list, predicate)
  if not predicate then
    if #list == 0 then error("Sequence contains no elements") end
    return list[#list]
  end
  for i = #list, 1, -1 do
    if predicate(list[i]) then return list[i] end
  end
  error("Sequence contains no matching element")
end

function List.LastOrDefault(list, predicate, default)
  if not predicate then
    if #list == 0 then return default end
    return list[#list]
  end
  for i = #list, 1, -1 do
    if predicate(list[i]) then return list[i] end
  end
  return default
end

function List.Min(list, selector)
  selector = selector or function(x) return x end
  local minVal = nil
  for i = 1, #list do
    local v = selector(list[i])
    if minVal == nil or v < minVal then minVal = v end
  end
  if minVal == nil then error("Sequence contains no elements") end
  return minVal
end

function List.Max(list, selector)
  selector = selector or function(x) return x end
  local maxVal = nil
  for i = 1, #list do
    local v = selector(list[i])
    if maxVal == nil or v > maxVal then maxVal = v end
  end
  if maxVal == nil then error("Sequence contains no elements") end
  return maxVal
end

function List.Sum(list, selector)
  selector = selector or function(x) return x end
  local total = 0
  for i = 1, #list do
    total = total + selector(list[i])
  end
  return total
end

function List.ToList(list)
  local copy = {}
  for i = 1, #list do copy[i] = list[i] end
  return copy
end

function List.ToDictionary(list, keySelector, valueSelector)
  valueSelector = valueSelector or function(x) return x end
  local dict = {}
  for i = 1, #list do
    local item = list[i]
    -- C# と同じく key → value の順で各 1 回評価する (Lua の代入式の
    -- 評価順は未規定なので local で明示する)。duplicate key は C# と
    -- 異なり上書き (既知差異、support-matrix 参照)
    local key = keySelector(item)
    dict[key] = valueSelector(item)
  end
  return dict
end

-- Dictionary operations
local Dict = {}
TinySystem.Dict = Dict

function Dict.new(init)
  return init or {}
end

function Dict.Add(dict, key, value)
  dict[key] = value
end

function Dict.Remove(dict, key)
  if dict[key] ~= nil then
    dict[key] = nil
    return true
  end
  return false
end

function Dict.ContainsKey(dict, key)
  return dict[key] ~= nil
end

function Dict.Count(dict)
  local n = 0
  for _ in pairs(dict) do n = n + 1 end
  return n
end

function Dict.Keys(dict)
  local keys = {}
  for k in pairs(dict) do keys[#keys + 1] = k end
  return keys
end

function Dict.Values(dict)
  local vals = {}
  for _, v in pairs(dict) do vals[#vals + 1] = v end
  return vals
end

-- Char (整数 code unit、ASCII の判定 / 変換。C backend も同じ表)
local Char = {}
TinySystem.Char = Char

function Char.IsDigit(c) return c >= 48 and c <= 57 end
function Char.IsUpper(c) return c >= 65 and c <= 90 end
function Char.IsLower(c) return c >= 97 and c <= 122 end
function Char.IsLetter(c) return Char.IsUpper(c) or Char.IsLower(c) end
function Char.IsLetterOrDigit(c) return Char.IsLetter(c) or Char.IsDigit(c) end
function Char.IsWhiteSpace(c) return c == 32 or (c >= 9 and c <= 13) end
function Char.ToUpper(c) if Char.IsLower(c) then return c - 32 end return c end
function Char.ToLower(c) if Char.IsUpper(c) then return c + 32 end return c end

-- String operations
local String = {}
TinySystem.String = String

function String.Contains(str, substr)
  return string.find(str, substr, 1, true) ~= nil
end

function String.IndexOf(str, value, start)
  local found = string.find(str, value, (start or 0) + 1, true)
  if not found then return -1 end
  return found - 1
end

function String.Join(sep, values, ...)
  if type(values) == "table" and select("#", ...) == 0 then
    return table.concat(values, sep)
  end

  local parts = { values, ... }
  return table.concat(parts, sep)
end

function String.Replace(str, old, new_str)
  if old == "" then
    error("oldValue cannot be empty", 2)
  end

  local result = {}
  local pos = 1
  while true do
    local start, stop = string.find(str, old, pos, true)
    if not start then
      result[#result + 1] = string.sub(str, pos)
      break
    end
    result[#result + 1] = string.sub(str, pos, start - 1)
    result[#result + 1] = new_str
    pos = stop + 1
  end
  return table.concat(result)
end

function String.StartsWith(str, prefix)
  return string.sub(str, 1, #prefix) == prefix
end

function String.EndsWith(str, suffix)
  if suffix == "" then return true end
  return string.sub(str, -#suffix) == suffix
end

function String.Trim(str)
  return string.match(str, "^%s*(.-)%s*$")
end

function String.Substring(str, start, length)
  if length then
    return string.sub(str, start + 1, start + length)
  else
    return string.sub(str, start + 1)
  end
end

function String.IsNullOrEmpty(s)
  return s == nil or s == ""
end

function String.Split(str, ...)
  local result = {}
  local argument_count = select("#", ...)
  local sep = ...
  if argument_count > 0 and (sep == nil or sep == "") then
    return { str }
  end

  local whitespace = argument_count == 0
  local pos = 1
  while true do
    local start, stop
    if whitespace then
      start, stop = string.find(str, "%s", pos)
    else
      start, stop = string.find(str, sep, pos, true)
    end
    if not start then
      result[#result + 1] = string.sub(str, pos)
      break
    end
    result[#result + 1] = string.sub(str, pos, start - 1)
    pos = stop + 1
  end
  return result
end

-- Math
local Math = {}
TinySystem.Math = Math

Math.PI = math.pi

function Math.Min(a, b) return math.min(a, b) end
function Math.Max(a, b) return math.max(a, b) end
function Math.Abs(x) return math.abs(x) end
function Math.Floor(x) return math.floor(x) end
function Math.Ceil(x) return math.ceil(x) end
function Math.Sqrt(x) return math.sqrt(x) end
function Math.Sin(x) return math.sin(x) end
function Math.Cos(x) return math.cos(x) end
function Math.Atan2(y, x) return math.atan(y, x) end
function Math.Pow(x, y) return x ^ y end
function Math.Tan(x) return math.tan(x) end
function Math.Exp(x) return math.exp(x) end
function Math.Log(x, base) return math.log(x, base) end

function Math.Sign(x)
  if x > 0 then return 1 end
  if x < 0 then return -1 end
  return 0
end

-- C# Math.Round: banker's rounding (midpoint rounds to even)
function Math.Round(x, digits)
  local scale = 10 ^ (digits or 0)
  local scaled = x * scale
  local floor = math.floor(scaled)
  local diff = scaled - floor
  local rounded
  if diff > 0.5 then
    rounded = floor + 1
  elseif diff < 0.5 then
    rounded = floor
  elseif floor % 2 == 0 then
    rounded = floor
  else
    rounded = floor + 1
  end
  if digits then return rounded / scale end
  return rounded
end

-- int.TryParse / float.TryParse の lowering 先: (found, value or default)
function Math.TryParseInt(s, default)
  local v = math.tointeger(tonumber(s))
  if v ~= nil then return true, v end
  return false, default
end

function Math.TryParseFloat(s, default)
  local v = tonumber(s)
  if v ~= nil then return true, v + 0.0 end
  return false, default
end

function Math.Clamp(value, min, max)
  if value < min then return min end
  if value > max then return max end
  return value
end

-- Random (System.Random 形: instance + Shared)。合意 PRNG は Lua 5.5 の
-- math.random (xoshiro256**、LUA_32BITS 構成。il-spec §13)。Shared は VM の
-- math.random 状態そのもの、instance は同じアルゴリズムの pure-Lua 実装
-- (64bit 値を 32bit 対で持つので 32bit / 64bit どちらの Lua でも同じ列)。
-- C backend は両方に同じ実装を持つので seed 固定時に bit 一致する
local Random = {}
Random.__index = Random
TinySystem.Random = Random

local M32 = 0xFFFFFFFF

local function shl64(h, l, n) -- 0 < n < 32
  return ((h << n) | (l >> (32 - n))) & M32, (l << n) & M32
end

local function rotl64(h, l, n) -- 0 < n < 32
  return ((h << n) | (l >> (32 - n))) & M32, ((l << n) | (h >> (32 - n))) & M32
end

local function rotr64(h, l, n) -- 0 < n < 32 (= rotl by 64 - n)
  return ((h >> n) | (l << (32 - n))) & M32, ((l >> n) | (h << (32 - n))) & M32
end

local function add64(h1, l1, h2, l2)
  local l = (l1 + l2) & M32
  local h = (h1 + h2) & M32
  if math.ult(l, l1) then h = (h + 1) & M32 end
  return h, l
end

-- xoshiro256** の 1 step。state は {h0, l0, h1, l1, h2, l2, h3, l3}
local function nextrand(s)
  local h0, l0, h1, l1, h2, l2, h3, l3 = s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8]
  -- res = rotl(s1 * 5, 7) * 9
  local th, tl = shl64(h1, l1, 2)
  th, tl = add64(th, tl, h1, l1)
  th, tl = rotl64(th, tl, 7)
  local rh, rl = shl64(th, tl, 3)
  rh, rl = add64(rh, rl, th, tl)
  local sh, sl = shl64(h1, l1, 17)
  h2, l2 = h2 ~ h0, l2 ~ l0
  h3, l3 = h3 ~ h1, l3 ~ l1
  h1, l1 = h1 ~ h2, l1 ~ l2
  h0, l0 = h0 ~ h3, l0 ~ l3
  h2, l2 = h2 ~ sh, l2 ~ sl
  h3, l3 = rotr64(h3, l3, 19) -- rotl 45
  s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8] = h0, l0, h1, l1, h2, l2, h3, l3
  return rh & M32, rl & M32
end

-- 32bit 符号付きへ (64bit Lua では上位を落として符号拡張、32bit Lua では恒等)
local function toi32(x)
  x = x & M32
  if math.ult(0x7FFFFFFF, x) then x = x - 0x100000000 end
  return x
end

-- math.randomseed(n1, n2) と同じ: state = {n1, 0xff, n2, 0}、先頭 16 値を捨てる
local function seedstate(n1, n2)
  local s = { 0, n1 & M32, 0, 0xff, 0, n2 & M32, 0, 0 }
  for _ = 1, 16 do nextrand(s) end
  return s
end

-- [0, n] (unsigned) への射影 (lmathlib の project)
local function project(s, ran, n)
  local lim = n
  local sh = 1
  while (lim & (lim + 1)) ~= 0 do
    lim = lim | (lim >> sh)
    sh = sh * 2
  end
  ran = ran & lim
  while math.ult(n, ran) do
    local _, l = nextrand(s)
    ran = l & lim
  end
  return ran
end

local function rangeof(s, low, up)
  local _, l = nextrand(s)
  if low > up then error("interval is empty") end
  local p = project(s, l, (up - low) & M32)
  return toi32((p + low) & M32)
end

local random_auto = 0

-- new Random() / new Random(seed)
function Random.new(seed)
  if seed == nil then
    random_auto = random_auto + 1
    -- 起動ごと・instance ごとに異なる seed (32bit Lua でも整数に収まる形)
    local t = math.tointeger(os.time()) or 0
    local c = math.tointeger(math.floor(os.clock() * 1000000)) or 0
    seed = (t ~ c ~ (random_auto * 0x9E3779B1)) & M32
  end
  return setmetatable({ s = seedstate(seed, 0) }, Random)
end

function Random:Next(min, max)
  if max then
    return rangeof(self.s, min, max - 1)
  elseif min then
    return rangeof(self.s, 0, min - 1)
  else
    return rangeof(self.s, 0, 2147483646)
  end
end

function Random:NextFloat()
  local h = nextrand(self.s)
  return (h >> 8) * (1 / 16777216)
end
Random.NextSingle = Random.NextFloat

function Random:Range(min, max)
  return rangeof(self.s, min, max)
end

-- Shared: VM の math.random 状態 (Random.Seed = math.randomseed)
local Shared = setmetatable({}, { __index = {
  Next = function(_, min, max)
    if max then
      return math.random(min, max - 1)
    elseif min then
      return math.random(0, min - 1)
    else
      return math.random(0, 2147483646)
    end
  end,
  NextFloat = function() return math.random() end,
  NextSingle = function() return math.random() end,
  Range = function(_, min, max) return math.random(min, max) end,
} })
Random.Shared = Shared

function Random.Seed(seed)
  math.randomseed(seed)
end

-- C# integer division / remainder (0 方向 truncation、剰余は被除数の符号)。
-- Lua の // と % は floor 由来で負数の結果が C# とずれるため、生成コードは
-- __tcs_idiv / __tcs_irem global 経由でこちらを使う。
function TinySystem.idiv(a, b)
  local q = a // b
  if a % b ~= 0 and (a < 0) ~= (b < 0) then
    q = q + 1
  end
  return q
end

function TinySystem.irem(a, b)
  return a - TinySystem.idiv(a, b) * b
end

-- C# の `is T` は「T またはその派生」。継承は instance の metatable =
-- class table、class table の metatable.__index = base で表現しているため
-- chain を辿る。生成コードは __tcs_is global 経由でこちらを使う。
function TinySystem.instanceof(x, T)
  local mt = getmetatable(x)
  while mt do
    if mt == T then return true end
    local link = getmetatable(mt)
    mt = link and link.__index
  end
  return false
end

-- TryGetValue の lowering 先 (il-spec §13)。(found, value or default) を返す
function Dict.TryGet(dict, key, default)
  local v = dict[key]
  if v ~= nil then return true, v end
  return false, default
end

-- `new T[n]` (値型 T): default を n 個詰めた sequence (生成コードは
-- __tcs_arr global 経由)。init が関数なら要素ごとに呼ぶ (struct の zero 値)
function TinySystem.arr(n, init)
  local t = {}
  if type(init) == "function" then
    for i = 1, n do t[i] = init() end
  else
    for i = 1, n do t[i] = init end
  end
  return t
end

-- C# のシフト (生成コードは __tcs_shl / __tcs_shr global 経由): count は
-- 31 でマスク、int の >> は算術シフト (Lua native は論理)
function TinySystem.shl(a, n)
  return a << (n & 31)
end

function TinySystem.shr(a, n)
  n = n & 31
  if a >= 0 then return a >> n end
  return ~((~a) >> n)
end

-- (int)f: 0 方向 truncation (生成コードは __tcs_trunc global 経由)
function TinySystem.trunc(x)
  local i = math.tointeger(x)
  if i then return i end
  if x >= 0 then return math.floor(x) end
  return math.ceil(x)
end

-- Nullable<T> (il-spec §13、module mode の __tcs_n* global の実体)
function TinySystem.nval(v)
  if v == nil then error("Nullable object must have a value") end
  return v
end
function TinySystem.nget(v, d) if v == nil then return d end return v end
function TinySystem.nlift(a, b, f) if a == nil or b == nil then return nil end return f(a, b) end
function TinySystem.nlift1(a, f) if a == nil then return nil end return f(a) end
function TinySystem.ncmp(a, b, f) if a == nil or b == nil then return false end return f(a, b) end
function TinySystem.nand(a, b)
  if a == false or b == false then return false end
  if a == nil or b == nil then return nil end
  return true
end
function TinySystem.nor(a, b)
  if a == true or b == true then return true end
  if a == nil or b == nil then return nil end
  return false
end
function TinySystem.nnot(a) if a == nil then return nil end return not a end
TinySystem.nops = {
  add = function(a, b) return a + b end,
  sub = function(a, b) return a - b end,
  mul = function(a, b) return a * b end,
  div = function(a, b) return a / b end,
  idiv = function(a, b) return TinySystem.idiv(a, b) end,
  irem = function(a, b) return TinySystem.irem(a, b) end,
  fmod = function(a, b) return math.fmod(a, b) end,
  band = function(a, b) return a & b end,
  bor = function(a, b) return a | b end,
  bxor = function(a, b) if type(a) == "boolean" then return a ~= b end return a ~ b end,
  shl = function(a, b) return TinySystem.shl(a, b) end,
  shr = function(a, b) return TinySystem.shr(a, b) end,
  lt = function(a, b) return a < b end,
  le = function(a, b) return a <= b end,
  gt = function(a, b) return a > b end,
  ge = function(a, b) return a >= b end,
  neg = function(a) return -a end,
  bnot = function(a) return ~a end,
}

-- f32 の shortest round-trip 10 進表記 (il-spec §13)
function TinySystem.fstr(v)
  if math.type(v) ~= "float" then return tostring(v) end
  local s = string.format("%.6g", v)
  if tonumber(s) == v then return s end
  s = string.format("%.8g", v)
  if tonumber(s) == v then return s end
  return string.format("%.9g", v)
end

-- T? の文字列化 (null → "")
function TinySystem.nstr(v)
  if v == nil then return "" end
  if math.type(v) == "float" then return TinySystem.fstr(v) end
  return tostring(v)
end

return TinySystem

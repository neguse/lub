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

function List.Remove(list, item)
  for i = 1, #list do
    if list[i] == item then
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

function List.Contains(list, item)
  for i = 1, #list do
    if list[i] == item then return true end
  end
  return false
end

function List.IndexOf(list, item)
  for i = 1, #list do
    if list[i] == item then return i - 1 end -- return 0-indexed
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

function Math.Clamp(value, min, max)
  if value < min then return min end
  if value > max then return max end
  return value
end

-- Random
local Random = {}
TinySystem.Random = Random

function Random.Next(min, max)
  if max then
    return math.random(min, max - 1)
  elseif min then
    return math.random(0, min - 1)
  else
    return math.random(0, 2147483646)
  end
end

function Random.NextFloat()
  return math.random()
end

function Random.Range(min, max)
  return math.random(min, max)
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

return TinySystem

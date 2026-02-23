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

function List.Count(list)
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
  if not predicate then return list[1] end
  for i = 1, #list do
    if predicate(list[i]) then return list[i] end
  end
  error("Sequence contains no matching element")
end

function List.FirstOrDefault(list, predicate)
  if not predicate then return list[1] end
  for i = 1, #list do
    if predicate(list[i]) then return list[i] end
  end
  return nil
end

function List.OrderBy(list, keySelector)
  local copy = {}
  for i = 1, #list do copy[i] = list[i] end
  table.sort(copy, function(a, b)
    return keySelector(a) < keySelector(b)
  end)
  return copy
end

function List.Min(list, selector)
  selector = selector or function(x) return x end
  local minVal = nil
  for i = 1, #list do
    local v = selector(list[i])
    if minVal == nil or v < minVal then minVal = v end
  end
  return minVal
end

function List.Max(list, selector)
  selector = selector or function(x) return x end
  local maxVal = nil
  for i = 1, #list do
    local v = selector(list[i])
    if maxVal == nil or v > maxVal then maxVal = v end
  end
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

function String.Replace(str, old, new_str)
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

function String.Split(str, sep)
  local result = {}
  if not sep or sep == "" then
    for i = 1, #str do
      result[i] = string.sub(str, i, i)
    end
    return result
  end
  local pos = 1
  while true do
    local start, stop = string.find(str, sep, pos, true)
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

-- HotReload (lume.hotswap-style in-place update for dofile)
local HotReload = {}
TinySystem.HotReload = HotReload

-- Recursively update old table in-place with new values,
-- keeping the old table identity so existing references survive.
local function deepUpdate(old, new, visited)
  if old == nil or new == nil then return end
  if visited[old] then return end
  visited[old] = true
  -- Update metatables
  local oldmt, newmt = getmetatable(old), getmetatable(new)
  if oldmt and newmt then deepUpdate(oldmt, newmt, visited) end
  -- Copy new keys/values into old table
  for k, v in pairs(new) do
    if type(v) == "table" and type(old[k]) == "table" then
      deepUpdate(old[k], v, visited)
    else
      old[k] = v
    end
  end
end

-- Swap a dofile'd script in-place.
-- All globals that were tables before reload get updated in-place,
-- so existing instances (whose metatables point to them) pick up new methods.
-- Returns true on success, or nil + error message on failure.
function HotReload.swap(filepath)
  -- Snapshot current globals (shallow clone)
  local oldglobals = {}
  for k, v in pairs(_G) do oldglobals[k] = v end

  local ok, err = xpcall(function()
    dofile(filepath)
  end, function(e) return tostring(e) end)

  if not ok then
    -- Rollback: restore globals
    for k in pairs(_G) do
      if oldglobals[k] == nil then _G[k] = nil end
    end
    for k, v in pairs(oldglobals) do _G[k] = v end
    return nil, err
  end

  -- For each global that was a table before AND got replaced with a new table:
  -- update the old table in-place, then put the old table back as the global.
  local visited = {}
  for k, oldval in pairs(oldglobals) do
    local newval = _G[k]
    if type(oldval) == "table" and type(newval) == "table" and oldval ~= newval then
      deepUpdate(oldval, newval, visited)
      _G[k] = oldval  -- restore old identity
    end
  end

  return true
end

return TinySystem

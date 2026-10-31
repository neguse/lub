-- TinyC# Runtime Library for Lua 5.5

local TinySystem = {}

-- List<T> operations
local List = {}
TinySystem.List = List

function List.new(init)
  return init or {}
end

-- 要素が null になりうる List の Add。長さを field `n` に持つ (# は nil の穴で測れない)。
-- `n` が無い table は nil を持たない (host の table、型引数の要素の Add を通っただけの List)
function List.AddCounted(list, item)
  local n = (list.n or #list) + 1
  list[n] = item
  list.n = n
end

-- 型引数の要素の List の Add。値型の List は `n` を持たないので、null を入れるまで作らない
function List.Add(list, item)
  local n = list.n
  if n == nil and item ~= nil then
    list[#list + 1] = item
  else
    n = (n or #list) + 1
    list[n] = item
    list.n = n
  end
end

-- Contains / IndexOf / Remove の等価 (EqualityComparer<T>.Default)。eq は
-- struct 要素の値等価 (型別 op_Equality)、省略時は raw == に加えて NaN 同士を
-- 等しいとみなす (float.Equals)。1-indexed の位置か nil
local function findItem(list, item, eq)
  local n = list.n or #list
  if eq then
    for i = 1, n do
      if eq(list[i], item) then return i end
    end
  elseif item == item then
    for i = 1, n do
      if list[i] == item then return i end
    end
  else
    for i = 1, n do
      local v = list[i]
      if v ~= v then return i end
    end
  end
  return nil
end

-- 1-indexed の位置を消して後ろを詰める。table.remove は null の入った列の長さを測れない
local function removeSlot(list, i, n)
  if i < n then table.move(list, i + 1, n, i) end
  list[n] = nil
  if list.n then list.n = n - 1 end
end

function List.Remove(list, item, eq)
  local i = findItem(list, item, eq)
  if i == nil then return false end
  removeSlot(list, i, list.n or #list)
  return true
end

function List.RemoveSlot(list, i)
  local n = list.n or #list
  if i < 1 or i > n then error("Index was out of range.") end
  removeSlot(list, i, n)
end

function List.Count(list)
  return list.n or #list
end

-- copy constructor `new List<T>(source)`。copy は変更可能な struct 要素の型別 __copy
function List.Copy(list, copy)
  local result = {n = list.n}
  if copy then
    for i = 1, list.n or #list do result[i] = copy(list[i]) end
  else
    table.move(list, 1, list.n or #list, 1, result)
  end
  return result
end

function List.Insert(list, index, item)
  local n = list.n or #list
  if index < 0 or index > n then error("Index must be within the bounds of the List.") end
  table.move(list, index + 1, n, index + 2)
  list[index + 1] = item
  if list.n or item == nil then list.n = n + 1 end
end

-- 新しい List に写す。copy は変更可能な struct 要素の型別 __copy
function List.GetRange(list, index, count, copy)
  local n = list.n or #list
  if index < 0 or count < 0 or count > n - index then
    error("Offset and length were out of bounds for the List.")
  end
  local result = {n = list.n and count}
  if copy then
    for i = 1, count do result[i] = copy(list[index + i]) end
  else
    table.move(list, index + 1, index + count, 1, result)
  end
  return result
end

function List.Contains(list, item, eq)
  return findItem(list, item, eq) ~= nil
end

function List.FindIndex(list, match)
  for i = 1, list.n or #list do
    if match(list[i]) then return i - 1 end
  end
  return -1
end

-- .NET の RemoveAll と同じく、述語は各要素に 1 回ずつ先頭から呼び、残す要素の順を保つ
-- .NET の List<T>.RemoveAll と同じ手順。述語が list を変えても同じ結果になるよう、長さは毎回読む
function List.RemoveAll(list, match)
  local free = 1
  while free <= List.Count(list) and not match(list[free]) do free = free + 1 end
  if free > List.Count(list) then return 0 end
  local current = free + 1
  while current <= List.Count(list) do
    while current <= List.Count(list) and match(list[current]) do current = current + 1 end
    if current <= List.Count(list) then
      list[free] = list[current]
      free = free + 1
      current = current + 1
    end
  end
  local n = List.Count(list)
  if n < free then return n - free + 1 end
  for i = n, free, -1 do list[i] = nil end
  if list.n then list.n = free - 1 end
  return n - free + 1
end

function List.IndexOf(list, item, eq)
  local i = findItem(list, item, eq)
  if i == nil then return -1 end
  return i - 1 -- 0-indexed
end

local function swapIfGreater(t, cmp, i, j)
  if cmp(t[i], t[j]) > 0 then t[i], t[j] = t[j], t[i] end
end

local function insertionSort(t, cmp, lo, n)
  for i = 0, n - 2 do
    local x = t[lo + i + 1]
    local j = i
    while j >= 0 and cmp(x, t[lo + j]) < 0 do
      t[lo + j + 1] = t[lo + j]
      j = j - 1
    end
    t[lo + j + 1] = x
  end
end

local function downHeap(t, cmp, lo, i, n)
  local d = t[lo + i - 1]
  while i <= n // 2 do
    local child = 2 * i
    if child < n and cmp(t[lo + child - 1], t[lo + child]) < 0 then child = child + 1 end
    if not (cmp(d, t[lo + child - 1]) < 0) then break end
    t[lo + i - 1] = t[lo + child - 1]
    i = child
  end
  t[lo + i - 1] = d
end

local function heapSort(t, cmp, lo, n)
  for i = n // 2, 1, -1 do downHeap(t, cmp, lo, i, n) end
  for i = n, 2, -1 do
    t[lo], t[lo + i - 1] = t[lo + i - 1], t[lo]
    downHeap(t, cmp, lo, 1, i - 1)
  end
end

-- 比較が不整合だと走査が区間の外に出る。
-- .NET はそこで例外 (IndexOutOfRangeException) を投げるので、同じ位置で止める
local function inconsistentComparison()
  error("Unable to sort because the IComparer.Compare() method returns inconsistent results")
end

local function pickPivotAndPartition(t, cmp, lo, n)
  local hi = n - 1
  local middle = hi // 2
  swapIfGreater(t, cmp, lo, lo + middle)
  swapIfGreater(t, cmp, lo, lo + hi)
  swapIfGreater(t, cmp, lo + middle, lo + hi)
  local pivot = t[lo + middle]
  t[lo + middle], t[lo + hi - 1] = t[lo + hi - 1], t[lo + middle]
  local left, right = 0, hi - 1
  while left < right do
    repeat
      left = left + 1
      if left > hi then inconsistentComparison() end
    until not (cmp(t[lo + left], pivot) < 0)
    repeat
      right = right - 1
      if right < 0 then inconsistentComparison() end
    until not (cmp(pivot, t[lo + right]) < 0)
    if left >= right then break end
    t[lo + left], t[lo + right] = t[lo + right], t[lo + left]
  end
  if left ~= hi - 1 then
    t[lo + left], t[lo + hi - 1] = t[lo + hi - 1], t[lo + left]
  end
  return left
end

local function introSort(t, cmp, lo, n, depthLimit)
  while n > 1 do
    if n <= 16 then
      if n == 2 then
        swapIfGreater(t, cmp, lo, lo + 1)
      elseif n == 3 then
        swapIfGreater(t, cmp, lo, lo + 1)
        swapIfGreater(t, cmp, lo, lo + 2)
        swapIfGreater(t, cmp, lo + 1, lo + 2)
      else
        insertionSort(t, cmp, lo, n)
      end
      return
    end
    if depthLimit == 0 then
      heapSort(t, cmp, lo, n)
      return
    end
    depthLimit = depthLimit - 1
    local p = pickPivotAndPartition(t, cmp, lo, n)
    introSort(t, cmp, lo + p + 1, n - p - 1, depthLimit)
    n = p
  end
end

local function introspectiveSort(t, cmp, lo, n)
  if n < 2 then return end
  local log2, m = 0, n
  while m > 1 do
    m = m // 2
    log2 = log2 + 1
  end
  introSort(t, cmp, lo, n, 2 * (log2 + 1))
end

-- 比較なしの順序。NaN は Sort の前に先頭へ寄せるので、ここには来ない
local function numberCompare(a, b)
  if a < b then return -1 end
  if a > b then return 1 end
  return 0
end

function List.Sort(list, comparison)
  local n = list.n or #list
  if comparison then
    introspectiveSort(list, comparison, 1, n)
    return
  end
  -- 比較なしの順序で null は最小 (.NET の既定の comparer)。残りを並べてから後ろへ置く
  if list.n then
    local rest, m = {}, 0
    for i = 1, n do
      local v = list[i]
      if v ~= nil then
        m = m + 1
        rest[m] = v
      end
    end
    if m < n then
      List.Sort(rest)
      for i = 1, n - m do list[i] = nil end
      table.move(rest, 1, m, n - m + 1, list)
      return
    end
  end
  if type(list[1]) == "string" then
    tcs_rt.sort_strings(list)
  else
    -- .NET の SortUtils.MoveNansToFront と同じく、NaN を先頭に寄せてから残りを並べる。
    -- 比較で等しい数のうち区別できるのは、float の ±0 と、subtype の違う整数と float だけ。
    -- どちらも無ければ等しい要素は区別できず、どの sort でも並びは同じなので C の table.sort を使う
    local nans, exact = 0, false
    local kind = math.type(list[1])
    for i = 1, n do
      local v = list[i]
      if v ~= v then
        nans = nans + 1
        list[nans], list[i] = v, list[nans]
      elseif math.type(v) ~= kind or (v == 0 and kind == "float") then
        exact = true
      end
    end
    if exact then
      introspectiveSort(list, numberCompare, nans + 1, n - nans)
    elseif nans == 0 then
      table.sort(list)
    else
      local rest = table.move(list, nans + 1, n, 1, {})
      table.sort(rest)
      table.move(rest, 1, n - nans, nans + 1, list)
    end
  end
end

-- Dictionary<float, _> の NaN key。Lua table は NaN を key にできないので、
-- .NET (NaN 同士は等しい key) と同じく 1 つの key として sentinel に置き、
-- 列挙で NaN に戻す (Dict.FloatKey / FloatKeyOut / FloatKeys。生成コードは
-- float key の dict にだけ通す)
local NAN_KEY = "NaN"

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

function Dict.FloatKey(key)
  if key ~= key then return NAN_KEY end
  return key
end

function Dict.FloatKeyOut(key)
  if key == NAN_KEY then return 0 / 0 end
  return key
end

function Dict.FloatKeys(dict)
  local keys = {}
  for k in pairs(dict) do keys[#keys + 1] = Dict.FloatKeyOut(k) end
  return keys
end

-- Queue<T>: 先頭と末尾の添字 (h / t) で Enqueue / Dequeue とも O(1)。空になると添字を戻す
local TcsQueue = {}
TinySystem.TcsQueue = TcsQueue

function TcsQueue.new()
  return {h = 1, t = 0}
end

function TcsQueue.Enqueue(queue, item)
  local t = queue.t + 1
  queue[t] = item
  queue.t = t
end

local function queueEmpty() error("Queue empty.") end

function TcsQueue.Dequeue(queue)
  local h, t = queue.h, queue.t
  if h > t then queueEmpty() end
  local item = queue[h]
  queue[h] = nil
  if h == t then
    queue.h, queue.t = 1, 0
  else
    queue.h = h + 1
  end
  return item
end

function TcsQueue.Peek(queue)
  if queue.h > queue.t then queueEmpty() end
  return queue[queue.h]
end

function TcsQueue.Count(queue)
  return queue.t - queue.h + 1
end

function TcsQueue.Clear(queue)
  for i = queue.h, queue.t do queue[i] = nil end
  queue.h, queue.t = 1, 0
end

-- 等価は List の検索と同じ (EqualityComparer<T>.Default。eq は struct 要素の op_Equality)
function TcsQueue.Contains(queue, item, eq)
  for i = queue.h, queue.t do
    local v = queue[i]
    if eq and eq(v, item) or not eq and (v == item or item ~= item and v ~= v) then return true end
  end
  return false
end

-- foreach が反復する今の要素の写し
function TcsQueue.ToList(queue)
  local list = table.move(queue, queue.h, queue.t, 1, {})
  list.n = queue.t - queue.h + 1
  return list
end

-- HashSet<T>: 要素 → slot の表 (map) と slot ごとの要素 (items)。列挙の順は .NET の HashSet と同じで、
-- slot の順。Remove した slot は free の鎖 (nexts) に積み、次の Add が最後に空いた slot から使う
local TcsHashSet = {}
TinySystem.TcsHashSet = TcsHashSet

-- Lua の table は nil を key にできないので null はこの key に置く
local NULL_KEY = {}

function TcsHashSet.new()
  return {map = {}, items = {}, nexts = {}, top = 0, free = 0, freeCount = 0}
end

function TcsHashSet.Add(set, item)
  local key = item
  if key == nil then key = NULL_KEY end
  if set.map[key] then return false end
  local slot = set.free
  if slot ~= 0 then
    set.free = set.nexts[slot]
    set.nexts[slot] = nil
    set.freeCount = set.freeCount - 1
  else
    slot = set.top + 1
    set.top = slot
  end
  set.items[slot] = item
  set.map[key] = slot
  return true
end

function TcsHashSet.Remove(set, item)
  local key = item
  if key == nil then key = NULL_KEY end
  local slot = set.map[key]
  if slot == nil then return false end
  set.map[key] = nil
  set.items[slot] = nil
  set.nexts[slot] = set.free
  set.free = slot
  set.freeCount = set.freeCount + 1
  return true
end

function TcsHashSet.Contains(set, item)
  if item == nil then item = NULL_KEY end
  return set.map[item] ~= nil
end

function TcsHashSet.Count(set)
  return set.top - set.freeCount
end

function TcsHashSet.Clear(set)
  set.map, set.items, set.nexts = {}, {}, {}
  set.top, set.free, set.freeCount = 0, 0, 0
end

-- foreach が反復する今の要素の写し
function TcsHashSet.ToList(set)
  local list, n = {}, 0
  for slot = 1, set.top do
    if set.nexts[slot] == nil then
      n = n + 1
      list[n] = set.items[slot]
    end
  end
  list.n = n
  return list
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
-- codepoint の UTF-8 文字列 (Rune の文字列化も同じ)。範囲外は CoreCLR の ArgumentOutOfRangeException に当たる fault
function Char.ConvertFromUtf32(c)
  if c < 0 or c > 0x10FFFF or (c >= 0xD800 and c <= 0xDFFF) then
    error("char.ConvertFromUtf32: not a Unicode scalar value")
  end
  return utf8.char(c)
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

-- enum の要素の Join。要素は String.FormatEnum (Enum.ToString と同じ member 名)
function String.JoinEnum(sep, values, spec)
  local parts = {}
  for i = 1, values.n or #values do parts[i] = String.FormatEnum(values[i], spec) end
  return String.Join(sep, parts)
end

-- 要素は連結と同じ文字列化 (float は TinySystem.fstr、bool は True / False)
function String.Join(sep, values, ...)
  if type(values) == "table" and select("#", ...) == 0 then
    local parts = {}
    for i = 1, values.n or #values do
      local v = values[i]
      if v == nil then
        v = ""
      elseif math.type(v) == "float" then
        v = TinySystem.fstr(v)
      elseif type(v) == "boolean" then
        v = TinySystem.bstr(v)
      elseif type(v) == "table" then
        error("ToString of this value is not supported")
      end
      parts[i] = v
    end
    return table.concat(parts, sep)
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

-- 1 本の "^%s*(.-)%s*$" は内側の空白の連続の長さの 2 乗の時間がかかるので、前と後ろを別に探す
function String.Trim(str)
  local first = string.find(str, "%S")
  if not first then return "" end
  return string.match(str, ".*%S", first)
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

-- 値は 0 との比較だけで使う (TCS1001 CompareOrdinalValue) ので -1 / 0 / 1 を返す。
-- null は他のどの string より小さい
function String.CompareOrdinal(a, b)
  if a == nil then return b == nil and 0 or -1 end
  if b == nil then return 1 end
  return tcs_rt.string_compare(a, b)
end

-- enum の文字列化 (Enum.ToString)。spec は IL builder が作る `名前:値` の列で、
-- Flags は先頭が `*` (il-spec §13)。spec ごとに 1 度だけ表へ展開する
local enumSpecs = {}

local function enumSpec(spec)
  local info = enumSpecs[spec]
  if info then return info end
  info = { flags = string.sub(spec, 1, 1) == "*", names = {}, values = {} }
  for name, value in string.gmatch(spec, "([^*,:]+):(-?%d+)") do
    info.names[#info.names + 1] = name
    info.values[#info.values + 1] = math.tointeger(tonumber(value)) or tonumber(value)
  end
  enumSpecs[spec] = info
  return info
end

-- 定義された値は名前、Flags は上位の member から含まれる bit を取った名前を
-- 昇順に ", " で繋ぐ。どちらでも表せない値は数値。T? の null は空文字列。
-- host の enum が Lua の値を数でなく持つとき ([LubLuaString] 等) はその値
function String.FormatEnum(v, spec)
  if v == nil then return "" end
  if type(v) ~= "number" then return tostring(v) end
  local info = enumSpec(spec)
  local names, values = info.names, info.values
  if not info.flags then
    for i = 1, #values do
      if values[i] == v then return names[i] end
    end
    return tostring(v)
  end
  if v == 0 then
    if values[1] == 0 then return names[1] end
    return "0"
  end
  local rest, found = v, {}
  for i = #values, 1, -1 do
    local cur = values[i]
    if cur ~= 0 and rest & cur == cur then
      rest = rest & ~cur
      table.insert(found, 1, names[i])
      if rest == 0 then return table.concat(found, ", ") end
    end
  end
  return tostring(v)
end

-- 補間の数値の標準書式 (il-spec §13)。spec は書式の文字と省略できる精度 ("G"、"E2" など)。
-- tcs_rt (runtime/lua/tcs_rt_lua.c) が .NET の ToString(format) と同じ文字列を作る。T? の null は空文字列
function String.FormatFloat(v, spec)
  if v == nil then return "" end
  return tcs_rt.ffmt(v, spec)
end

function String.FormatInt(v, spec)
  if v == nil then return "" end
  return tcs_rt.ifmt(v, spec)
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

-- MathF.Min / Max (IEEE 754:2019 minimum / maximum): NaN を伝播し、-0 を
-- +0 より小さいとみなす。int の overload も同じ関数 (int に NaN / -0 は無い)
function Math.Min(a, b)
  if a < b then return a end
  if b < a then return b end
  if a ~= a or (a == 0 and 1 / a < 0) then return a end
  return b
end
function Math.Max(a, b)
  if a > b then return a end
  if b > a then return b end
  if a ~= a or (b == 0 and 1 / b < 0) then return a end
  return b
end
function Math.Abs(x) return math.abs(x) end
-- math.floor / math.ceil は整数へ変換して -0 の符号を落とすので、float の
-- まま丸める // を使う
function Math.Floor(x) return x // 1 end
function Math.Ceil(x) return -(-x // 1) end
Math.Ceiling = Math.Ceil
function Math.Sqrt(x) return math.sqrt(x) end
-- 超越関数は host が登録する tcs_math (runtime/math の C。tcs2c と .NET の
-- TinySystem.Math と同じ実装) を呼び、どの経路でも bit 単位で同じ結果にする
function Math.Sin(x) return tcs_math.sin(x) end
function Math.Cos(x) return tcs_math.cos(x) end
function Math.Atan2(y, x) return tcs_math.atan2(y, x) end
-- 指数 2 は乗算 (Lua の ^ と tcs2c と同じ)。integer の x は float にしてから掛ける
function Math.Pow(x, y)
  if y == 2 then
    x = x * 1.0
    return x * x
  end
  return tcs_math.pow(x, y)
end
function Math.Tan(x) return tcs_math.tan(x) end
function Math.Exp(x) return tcs_math.exp(x) end
-- MathF.Log(x, base): base が 1、または x が 1 でなく base が 0 / +inf なら NaN。
-- 底 2 / 10 は専用の関数、ほかは log(x) / log(base) (Lua の math.log と同じ分け方)
function Math.Log(x, base)
  if base == nil then return tcs_math.log(x) end
  if base == 1 or (x ~= 1 and (base == 0 or base == math.huge)) then
    return 0.0 / 0.0
  end
  if base == 2 then return tcs_math.log2(x) end
  if base == 10 then return tcs_math.log10(x) end
  return tcs_math.log(x) / tcs_math.log(base)
end

function Math.Sign(x)
  if x > 0 then return 1 end
  if x < 0 then return -1 end
  return 0
end

-- C# の int / float の CompareTo: -1 / 0 / 1。NaN は他のどの値よりも小さく、
-- NaN 同士は 0 (±0 は 0)
function Math.CompareTo(a, b)
  if a < b then return -1 end
  if a > b then return 1 end
  if a == b then return 0 end
  if a ~= a then return b ~= b and 0 or -1 end
  return 1
end

-- C# Math.Round: banker's rounding (midpoint rounds to even)。0 に丸めた
-- 結果は x の符号を持つ (Round(-0.25) は -0)
function Math.Round(x, digits)
  local scale = 10 ^ (digits or 0)
  local scaled = x * scale
  local floor = scaled // 1
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
  if rounded == 0 then rounded = scaled * 0.0 end
  if digits then return rounded / scale end
  return rounded
end

-- int.TryParse / float.TryParse の lowering 先: (found, value or default)
-- int は NumberStyles.Integer (前後の空白、先頭の符号、10 進数字だけ)。
-- tonumber は小数・指数・16 進も読むので書式を先に絞る。i32 を超える数字列は
-- LUA_32BITS では float になる (丸めで範囲内に見える) ので integer に限る
function Math.TryParseInt(s, default)
  local digits = s and string.match(s, "^%s*([+-]?%d+)%s*$")
  local v = digits and tonumber(digits)
  if math.type(v) == "integer" and v >= -2147483647 - 1 and v <= 2147483647 then
    return true, v
  end
  return false, default
end

-- tonumber は整数の文字列を integer で返すので float にする (* 1.0 は
-- "-0.0" の -0 を保つ)
function Math.TryParseFloat(s, default)
  local v = tonumber(s)
  if v ~= nil then return true, v * 1.0 end
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
    -- 起動ごと・instance ごとに異なる seed。os.clock は 2^31 で畳み、CPU 時間が長くなっても 32bit Lua の整数に収める
    local t = math.tointeger(os.time()) or 0
    local c = math.tointeger(math.floor(os.clock() * 1000000 % 2147483648)) or 0
    seed = (t ~ c ~ (random_auto * 0x9E3779B1)) & M32
  end
  return setmetatable({ s = seedstate(seed, 0) }, Random)
end

-- Next の上限は排他。空の範囲 (max == min) は .NET と同じく min を返す
local function lastof(min, max)
  if max == min then return min end
  return max - 1
end

function Random:Next(min, max)
  if max then
    return rangeof(self.s, min, lastof(min, max))
  elseif min then
    return rangeof(self.s, 0, lastof(0, min))
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
      return math.random(min, lastof(min, max))
    elseif min then
      return math.random(0, lastof(0, min))
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

-- List / 配列 / string の添字 (il-spec §12)。生成コードは __tcs_get / __tcs_set / __tcs_byte の
-- global 経由でこちらを使う。値型の要素は nil を持たないので、範囲内の key は必ず値を持つ。
-- 要素が null になりうる List は __tcs_lget / __tcs_lset / __tcs_lchk で長さと比べる
local function outOfRange() error("Index was out of range.") end

function TinySystem.get(t, i)
  local v = t[i + 1]
  if v == nil then outOfRange() end
  return v
end

function TinySystem.set(t, i, v)
  if t[i + 1] == nil then outOfRange() end
  t[i + 1] = v
end

function TinySystem.count(t)
  return t.n or #t
end

function TinySystem.lchk(t, i)
  if i < 0 or i >= (t.n or #t) then outOfRange() end
end

function TinySystem.lget(t, i)
  local v = t[i + 1]
  if not v then TinySystem.lchk(t, i) end
  return v
end

function TinySystem.lset(t, i, v)
  if t[i + 1] == nil then TinySystem.lchk(t, i) end
  t[i + 1] = v
end

-- string.byte は負の位置を末尾から数えるので、1 未満は範囲外にする
function TinySystem.byte(s, k)
  local b = k > 0 and string.byte(s, k)
  if not b then outOfRange() end
  return b
end

-- checked(expr) の i32 の + - * と単項 - (il-spec §5)。生成コードは __tcs_cadd などの
-- global 経由でこちらを使う。LUA_32BITS の integer は wrap するので、wrap の向きでも検出する
local function overflow() error("Arithmetic operation resulted in an overflow.") end

function TinySystem.cadd(a, b)
  local s = a + b
  if s > 2147483647 or s < -2147483648 or (b < 0) ~= (s < a) then overflow() end
  return s
end

function TinySystem.csub(a, b)
  local d = a - b
  if d > 2147483647 or d < -2147483648 or (b > 0) ~= (d < a) then overflow() end
  return d
end

function TinySystem.cmul(a, b)
  local p = a * b
  if p > 2147483647 or p < -2147483648
      or a ~= 0 and (p // a ~= b or a == -1 and b == -2147483648) then overflow() end
  return p
end

function TinySystem.cneg(a)
  if a == -2147483648 then overflow() end
  return -a
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

-- class の interface の検査。class table の __interfaces は実装する interface の名前の集合。
-- 生成コードは __tcs_implements global 経由でこちらを使う。
function TinySystem.implements(x, name)
  local mt = getmetatable(x)
  local set = mt and mt.__interfaces
  return set ~= nil and set[name] == true
end

-- TryGetValue の lowering 先 (il-spec §13)。(found, value or default) を返す
function Dict.TryGet(dict, key, default)
  local v = dict[key]
  if v ~= nil then return true, v end
  return false, default
end

-- `new T[n]` (値型 T): default を n 個詰めた sequence (生成コードは
-- __tcs_arr global 経由)。init が関数なら要素ごとに呼ぶ (struct の zero 値)
-- struct を入れた型引数の既定値は zero 値と copy の作り方 (記述子 `{__tcs_td = true, n, c}`)。
-- 生成コードは __tcs_dv / __tcs_tc global 経由
function TinySystem.dv(d)
  if type(d) == "table" and d.__tcs_td then return d.n() end
  return d
end

-- `new T()` の型引数。class を入れた型引数は作り方を持たない (既定値は null)
function TinySystem.nt(d)
  if d == nil then error("new T() of a class type argument is not supported") end
  return TinySystem.dv(d)
end

-- enum を入れた型引数の記述子 (名前の表ごとに 1 つ)。生成コードは __tcs_etd global 経由
local enumDescriptors = {}
function TinySystem.etd(spec)
  local d = enumDescriptors[spec]
  if d then return d end
  d = {__tcs_td = true, n = function() return 0 end, c = function(v) return v end,
    s = function(v) return String.FormatEnum(v, spec) end}
  enumDescriptors[spec] = d
  return d
end

-- 型引数の値の文字列化。記述子が名前の引き方 (enum) を持てば使う
function TinySystem.tstr(d, v)
  if type(d) == "table" and d.s then return d.s(v) end
  return TinySystem.nstr(v)
end

function TinySystem.tc(d, v)
  if type(d) == "table" and d.__tcs_td then return d.c(v) end
  return v
end

function TinySystem.arr(n, init)
  local t = {}
  if type(init) == "function" then
    for i = 1, n do t[i] = init() end
  elseif type(init) == "table" and init.__tcs_td then
    for i = 1, n do t[i] = init.n() end
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

-- (char)x: char は 1 byte の code unit (生成コードは __tcs_char global 経由)
function TinySystem.char(x)
  if x < 0 or x > 255 then error("(char) cast: value is not in 0-255; use char.ConvertFromUtf32 for a codepoint") end
  return x
end

-- (int)f: 0 方向 truncation (生成コードは __tcs_trunc global 経由)
function TinySystem.trunc(x)
  local i = math.tointeger(x)
  if i then return i end
  if x >= 0 then return math.floor(x) end
  return math.ceil(x)
end

-- float.Parse の結果 (tonumber は整数の文字列を integer で返す) を float に
-- する。不正な文字列の nil はそのまま (生成コードは __tcs_tofloat global 経由)
function TinySystem.tofloat(v)
  if v == nil then return nil end
  return v * 1.0
end

-- static method group の wrapper (module mode の __tcs_mg global の実体)。
-- struct parameter を copy してから呼ぶ。
-- c は parameter ごとの copy 関数 (不要なら false)。
-- 同じ関数には同じ wrapper を返す
local mg_cache = setmetatable({}, { __mode = "k" })
function TinySystem.mg(f, c1, c2, ...)
  local w = mg_cache[f]
  if w then return w end
  if c2 == nil then
    w = function(a) return f(c1(a)) end
  elseif select("#", ...) == 0 then
    w = function(a, b)
      if c1 then a = c1(a) end
      if c2 then b = c2(b) end
      return f(a, b)
    end
  else
    local cs = { c1, c2, ... }
    w = function(...)
      local args = table.pack(...)
      for i = 1, #cs do
        if cs[i] then args[i] = cs[i](args[i]) end
      end
      return f(table.unpack(args, 1, args.n))
    end
  end
  mg_cache[f] = w
  return w
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
  tofloat = function(a) return a * 1.0 end,
  bnot = function(a) return ~a end,
}

-- f32 の既定の文字列化 (il-spec §13)。
-- host が登録する tcs_rt (runtime/lua/tcs_rt_lua.c) が .NET の float.ToString() と同じ文字列を作る
function TinySystem.fstr(v) return tcs_rt.fstr(v) end

-- bool は .NET と同じ True / False
function TinySystem.bstr(b) return b and "True" or "False" end
-- 生成コードは __tcs_tupleN global 経由 (multi-return のタプルを保持するときの struct の table)
function TinySystem.tuple2(v1, v2) return {Item1 = v1, Item2 = v2} end
function TinySystem.tuple3(v1, v2, v3) return {Item1 = v1, Item2 = v2, Item3 = v3} end
function TinySystem.tuple4(v1, v2, v3, v4) return {Item1 = v1, Item2 = v2, Item3 = v3, Item4 = v4} end
function TinySystem.tuple5(v1, v2, v3, v4, v5)
  return {Item1 = v1, Item2 = v2, Item3 = v3, Item4 = v4, Item5 = v5}
end
function TinySystem.tuple6(v1, v2, v3, v4, v5, v6)
  return {Item1 = v1, Item2 = v2, Item3 = v3, Item4 = v4, Item5 = v5, Item6 = v6}
end
function TinySystem.tuple7(v1, v2, v3, v4, v5, v6, v7)
  return {Item1 = v1, Item2 = v2, Item3 = v3, Item4 = v4, Item5 = v5, Item6 = v6, Item7 = v7}
end

-- T? と object と型引数の文字列化 (null → "")。class の値は ToString の override を呼ぶ。
-- 他の table の値はサブセット外 (TCS1001 UnsupportedToString)
function TinySystem.nstr(v)
  if v == nil then return "" end
  if math.type(v) == "float" then return TinySystem.fstr(v) end
  if type(v) == "boolean" then return TinySystem.bstr(v) end
  if type(v) == "table" then
    local mt = getmetatable(v)
    if mt and mt.ToString then return mt.ToString(v) or "" end
    error("ToString of this value is not supported")
  end
  return tostring(v)
end

return TinySystem

-- bench/player-apply.lua — bridge 方式の player 側 apply コスト baseline (T172)
--
-- 計測系列 (doc/incremental-module-compilation-design.md §14.2 / §15):
--   load:    生成 Lua bundle 相当ソースの load() (parse のみ、実行なし)
--   walk-*:  lume.hotswap が成功時に必ず実行する update(oldmod, newmod) の走査コスト。
--            entry が type table を直接 return する場合(walk-type-table)と
--            function のみの thin wrapper を return する場合(walk-wrapper)と
--            old==new fast-path を入れた update(walk-fastpath)を比較する。
--
-- 実行: deps/lua/lua bench/player-apply.lua
-- 出力: 系列ごとの p50/p95/max (ms)。live state 規模は sprite 数で変える。

local RUNS = 30

-- ---------------------------------------------------------------------------
-- rxi/lume (MIT) hotswap 内の update を計測用に忠実に複製。
-- 参照実装: https://github.com/rxi/lume lume.hotswap
local function lume_update_walk(oldmod, newmod)
  local updated = {}
  local function update(old, new)
    if updated[old] then return end
    updated[old] = true
    local oldmt, newmt = getmetatable(old), getmetatable(new)
    if oldmt and newmt then update(oldmt, newmt) end
    for k, v in pairs(new) do
      if type(v) == "table" then update(old[k], v) else old[k] = v end
    end
  end
  update(oldmod, newmod)
end

-- 同上 + old == new fast-path (lub への feature request 案)
local function lume_update_walk_fastpath(oldmod, newmod)
  local updated = {}
  local function update(old, new)
    if old == new then return end
    if updated[old] then return end
    updated[old] = true
    local oldmt, newmt = getmetatable(old), getmetatable(new)
    if oldmt and newmt then update(oldmt, newmt) end
    for k, v in pairs(new) do
      if type(v) == "table" then update(old[k], v) else old[k] = v end
    end
  end
  update(oldmod, newmod)
end

-- ---------------------------------------------------------------------------
local function percentile(sorted, p)
  local idx = math.max(1, math.ceil(#sorted * p))
  return sorted[idx]
end

local function bench(name, n, fn)
  local samples = {}
  fn() -- warm up (alloc/JIT なしでも table 生成をならす)
  for i = 1, n do
    local t0 = os.clock()
    fn()
    samples[#samples + 1] = (os.clock() - t0) * 1000
  end
  table.sort(samples)
  print(string.format("%-28s p50 %8.3f ms  p95 %8.3f ms  max %8.3f ms  (n=%d)",
    name, percentile(samples, 0.5), percentile(samples, 0.95), samples[#samples], n))
end

-- ---------------------------------------------------------------------------
-- 系列 1: bundle load (parse のみ)。emitter 出力風の合成ソースを規模別に生成する。
local function synth_bundle(classes, methods_per_class)
  local out = {"-- synthetic bundle\n"}
  for c = 1, classes do
    local name = "Class" .. c
    out[#out + 1] = name .. " = {}\n" .. name .. ".__index = " .. name .. "\n"
    out[#out + 1] = "function " .. name .. ".new(x, y)\n" ..
      "  local self = setmetatable({}, " .. name .. ")\n" ..
      "  self.x = x\n  self.y = y\n  return self\nend\n"
    for m = 1, methods_per_class do
      out[#out + 1] = "function " .. name .. ":m" .. m .. "(a, b)\n" ..
        "  local t = (a or 0) + (b or 1) * " .. m .. "\n" ..
        "  if t > 10 then return t - self.x end\n" ..
        "  return t + self.y\nend\n"
    end
  end
  out[#out + 1] = "return Class1\n"
  return table.concat(out)
end

print("== load (parse only) ==")
for _, cfg in ipairs({
  { classes = 8,   methods = 12 },  -- 小サンプル級
  { classes = 60,  methods = 20 },  -- playground full bundle 級 (~150KB)
  { classes = 180, methods = 20 },  -- 成長時 (~450KB)
}) do
  local src = synth_bundle(cfg.classes, cfg.methods)
  bench(string.format("load %3dKB", math.floor(#src / 1024)), RUNS, function()
    assert(load(src, "@bench.lua", "t"))
  end)
end

-- ---------------------------------------------------------------------------
-- 系列 2: lume update 走査。
-- 現行 emitter 表現に合わせる: class table 自体が instance metatable。
-- static field (sprite list) は class table 直付け。
local function make_live_graph(sprite_count)
  local Sprite = {}
  Sprite.__index = Sprite
  local Entry = {}
  Entry.__index = Entry
  Entry.sprites = {}
  for i = 1, sprite_count do
    Entry.sprites[i] = setmetatable(
      { x = i, y = i * 2, vx = 0.1, vy = 0.2, life = 3.0, r = 0.5 }, Sprite)
  end
  Entry.onFrame = function() end
  Entry.onInit = function() end
  return Entry
end

print("== lume update walk ==")
for _, count in ipairs({ 10000, 100000, 200000 }) do
  local entry = make_live_graph(count)
  bench(string.format("walk-type-table %6dspr", count), RUNS, function()
    lume_update_walk(entry, entry)
  end)
  bench(string.format("walk-fastpath   %6dspr", count), RUNS, function()
    lume_update_walk_fastpath(entry, entry)
  end)
end

local wrapper = {
  onInit = function() end,
  onFrame = function() end,
  onEvent = function() end,
  onQuit = function() end,
}
bench("walk-wrapper", RUNS, function()
  lume_update_walk(wrapper, wrapper)
end)

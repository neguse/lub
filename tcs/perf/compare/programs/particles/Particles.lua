-- Game-style: particles with gravity, wall bounce and a pull toward the centre
local state = 12345

local function lcg()
  state = (state * 1103515245 + 12345) & 0x3FFFFFFF
  return state
end

local function frand(lo, hi)
  return lo + (lcg() % 1000) * (1.0 / 1000.0) * (hi - lo)
end

-- float to int, truncating toward zero like the C# cast
local function trunc(x)
  return x >= 0 and math.floor(x) or math.ceil(x)
end

local frames = tonumber(os.getenv("BENCH_N")) or 100
local n = 4096
local dt = 0.02

local ps = {}
for i = 1, n do
  local p = { px = 0.0, py = 0.0, vx = 0.0, vy = 0.0 }
  p.px = frand(100.0, 300.0)
  p.py = frand(50.0, 200.0)
  p.vx = frand(-40.0, 40.0)
  p.vy = frand(-40.0, 40.0)
  ps[i] = p
end

for _ = 1, frames do
  for i = 1, n do
    local p = ps[i]
    p.vy = p.vy + 98.0 * dt
    p.px = p.px + p.vx * dt
    p.py = p.py + p.vy * dt
    if p.px < 0.0 then p.px = 0.0; p.vx = -p.vx * 0.9 end
    if p.px > 400.0 then p.px = 400.0; p.vx = -p.vx * 0.9 end
    if p.py < 0.0 then p.py = 0.0; p.vy = -p.vy * 0.9 end
    if p.py > 240.0 then p.py = 240.0; p.vy = -p.vy * 0.9 end
    local dx = p.px - 200.0
    local dy = p.py - 120.0
    local d2 = dx * dx + dy * dy
    if d2 < 400.0 then
      p.px = p.px + dx * 0.1
      p.py = p.py + dy * 0.1
    end
  end
end

local sx, sy = 0.0, 0.0
for i = 1, n do
  sx = sx + ps[i].px
  sy = sy + ps[i].py
end
print("sum x: " .. trunc(sx) .. ", sum y: " .. trunc(sy))

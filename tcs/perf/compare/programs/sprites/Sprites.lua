-- Game-style: a list of sprite tables moved every frame, bouncing at the screen edges
local state = 12345

local function lcg()
  state = (state * 1103515245 + 12345) & 0x3FFFFFFF
  return state
end

local function frand(lo, hi)
  return lo + (lcg() % 1000) * (1.0 / 1000.0) * (hi - lo)
end

local function update(s, dt)
  s.x = s.x + s.vx * dt
  s.y = s.y + s.vy * dt
  if s.x < 0.0 then s.x = 0.0; s.vx = -s.vx end
  if s.x > 400.0 then s.x = 400.0; s.vx = -s.vx end
  if s.y < 0.0 then s.y = 0.0; s.vy = -s.vy end
  if s.y > 240.0 then s.y = 240.0; s.vy = -s.vy end
  s.frame = (s.frame + 1) % 8
end

-- float to int, truncating toward zero like the C# cast
local function trunc(x)
  return x >= 0 and math.floor(x) or math.ceil(x)
end

local frames = tonumber(os.getenv("BENCH_N")) or 100
local count = 1024
local dt = 0.02

local sprites = {}
for _ = 1, count do
  local sp = { x = 0.0, y = 0.0, vx = 0.0, vy = 0.0, frame = 0 }
  sp.x = frand(0.0, 400.0)
  sp.y = frand(0.0, 240.0)
  sp.vx = frand(-60.0, 60.0)
  sp.vy = frand(-60.0, 60.0)
  sp.frame = lcg() % 8
  sprites[#sprites + 1] = sp
end

for _ = 1, frames do
  for _, sp in ipairs(sprites) do update(sp, dt) end
end

local sx, sy, sf = 0.0, 0.0, 0
for _, sp in ipairs(sprites) do
  sx = sx + sp.x
  sy = sy + sp.y
  sf = sf + sp.frame
end
print("sum x: " .. trunc(sx) .. ", sum y: " .. trunc(sy) .. ", sum frame: " .. sf)

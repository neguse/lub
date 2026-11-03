-- spectral-norm, same steps as the C# version
-- i and j are 1-based here, so i + j - 2 is the C# i + j
local function A(i, j)
  local ij = i + j - 2
  return 1.0 / (ij * (ij + 1) // 2 + i)
end

local function multiplyAv(n, v, av)
  for i = 1, n do
    local sum = 0.0
    for j = 1, n do sum = sum + A(i, j) * v[j] end
    av[i] = sum
  end
end

local function multiplyAtv(n, v, atv)
  for i = 1, n do
    local sum = 0.0
    for j = 1, n do sum = sum + A(j, i) * v[j] end
    atv[i] = sum
  end
end

local function multiplyAtAv(n, v, tmp, atav)
  multiplyAv(n, v, tmp)
  multiplyAtv(n, tmp, atav)
end

-- float to int, truncating toward zero like the C# cast
local function trunc(x)
  return x >= 0 and math.floor(x) or math.ceil(x)
end

local function run(n, show)
  local u, v, tmp = {}, {}, {}
  for i = 1, n do
    u[i] = 1.0
    v[i] = 0.0
    tmp[i] = 0.0
  end
  for _ = 1, 10 do
    multiplyAtAv(n, u, tmp, v)
    multiplyAtAv(n, v, tmp, u)
  end
  local vBv, vv = 0.0, 0.0
  for i = 1, n do
    vBv = vBv + u[i] * v[i]
    vv = vv + v[i] * v[i]
  end
  local result = trunc(math.sqrt(vBv / vv) * 1000000.0)
  if show then print("spectral norm x 1e6: " .. result) end
  return result
end

local n = tonumber(os.getenv("BENCH_N")) or 100
-- BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
local reps = tonumber(os.getenv("BENCH_REPS")) or 1
local prev = 0
for r = 0, reps - 1 do prev = run(prev == -1 and n + 1 or n, r == reps - 1) end

-- Recursive call speed: naive double recursion
local function calc(n)
  if n < 2 then return n end
  return calc(n - 1) + calc(n - 2)
end

local function run(n, show)
  local f = calc(n)
  if show then print("fib(" .. n .. ") = " .. f) end
  return f
end

local n = tonumber(os.getenv("BENCH_N")) or 30
-- BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
local reps = tonumber(os.getenv("BENCH_REPS")) or 1
local prev = 0
for r = 0, reps - 1 do prev = run(prev == -1 and n + 1 or n, r == reps - 1) end

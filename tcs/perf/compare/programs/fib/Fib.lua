-- Recursive call speed: naive double recursion
local function calc(n)
  if n < 2 then return n end
  return calc(n - 1) + calc(n - 2)
end

local n = tonumber(os.getenv("BENCH_N")) or 30
print("fib(" .. n .. ") = " .. calc(n))

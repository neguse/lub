-- mandelbrot: same range and 50 iterations as the C# version
local n = tonumber(os.getenv("BENCH_N")) or 200
local inside = 0
for y = 0, n - 1 do
  local ci = 2.0 * y / n - 1.0
  for x = 0, n - 1 do
    local cr = 2.0 * x / n - 1.5
    local zr, zi, tr, ti = 0.0, 0.0, 0.0, 0.0
    local i = 0
    while i < 50 and tr + ti <= 4.0 do
      zi = 2.0 * zr * zi + ci
      zr = tr - ti + cr
      tr = zr * zr
      ti = zi * zi
      i = i + 1
    end
    if tr + ti <= 4.0 then inside = inside + 1 end
  end
end
print("inside: " .. inside .. " of " .. n * n)

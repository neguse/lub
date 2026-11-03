-- fannkuch-redux (single-threaded), same steps as the C# version
-- Arrays are 1-based: perm holds 1..n and index i here is index i-1 in the C#.
local function run(n, show)
  local perm, perm1, count = {}, {}, {}
  for i = 1, n do perm1[i] = i end

  local maxFlips = 0
  local checksum = 0
  local permCount = 0
  local r = n
  while true do
    while r ~= 1 do
      count[r] = r
      r = r - 1
    end

    for i = 1, n do perm[i] = perm1[i] end
    local flips = 0
    local k = perm[1]
    while k ~= 1 do
      local lo, hi = 1, k
      while lo < hi do
        perm[lo], perm[hi] = perm[hi], perm[lo]
        lo = lo + 1
        hi = hi - 1
      end
      flips = flips + 1
      k = perm[1]
    end
    if flips > maxFlips then maxFlips = flips end
    if permCount % 2 == 0 then
      checksum = checksum + flips
    else
      checksum = checksum - flips
    end

    while true do
      if r == n then
        if show then
          print(checksum)
          print("Pfannkuchen(" .. n .. ") = " .. maxFlips)
        end
        return checksum
      end
      local perm0 = perm1[1]
      for i = 1, r do perm1[i] = perm1[i + 1] end
      perm1[r + 1] = perm0
      count[r + 1] = count[r + 1] - 1
      if count[r + 1] > 0 then break end
      r = r + 1
    end
    permCount = permCount + 1
  end
end

local n = tonumber(os.getenv("BENCH_N")) or 7
-- BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
local reps = tonumber(os.getenv("BENCH_REPS")) or 1
local prev = 0
for r = 0, reps - 1 do prev = run(prev == -1 and n + 1 or n, r == reps - 1) end

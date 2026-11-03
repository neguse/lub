-- binary-trees (single-threaded); a node is a table {left, right}, a leaf has both nil
local function make(depth)
  if depth == 0 then return {} end
  return { make(depth - 1), make(depth - 1) }
end

local function check(node)
  if node[1] == nil then return 1 end
  return 1 + check(node[1]) + check(node[2])
end

local function run(n, show)
  local minDepth = 4
  local maxDepth = n < minDepth + 2 and minDepth + 2 or n

  local stretch = maxDepth + 1
  local stretchCheck = check(make(stretch))
  if show then print("stretch tree of depth " .. stretch .. "\t check: " .. stretchCheck) end

  local digest = stretchCheck
  local longLived = make(maxDepth)
  for d = minDepth, maxDepth, 2 do
    local iterations = 1 << (maxDepth - d + minDepth)
    local chk = 0
    for _ = 1, iterations do chk = chk + check(make(d)) end
    if show then print(iterations .. "\t trees of depth " .. d .. "\t check: " .. chk) end
    digest = digest + chk
  end
  local longCheck = check(longLived)
  if show then print("long lived tree of depth " .. maxDepth .. "\t check: " .. longCheck) end
  return digest + longCheck
end

local n = tonumber(os.getenv("BENCH_N")) or 10
-- BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
local reps = tonumber(os.getenv("BENCH_REPS")) or 1
local prev = 0
for r = 0, reps - 1 do prev = run(prev == -1 and n + 1 or n, r == reps - 1) end

-- binary-trees (single-threaded); a node is a table {left, right}, a leaf has both nil
local function make(depth)
  if depth == 0 then return {} end
  return { make(depth - 1), make(depth - 1) }
end

local function check(node)
  if node[1] == nil then return 1 end
  return 1 + check(node[1]) + check(node[2])
end

local n = tonumber(os.getenv("BENCH_N")) or 10
local minDepth = 4
local maxDepth = n < minDepth + 2 and minDepth + 2 or n

local stretch = maxDepth + 1
print("stretch tree of depth " .. stretch .. "\t check: " .. check(make(stretch)))

local longLived = make(maxDepth)
for d = minDepth, maxDepth, 2 do
  local iterations = 1 << (maxDepth - d + minDepth)
  local chk = 0
  for _ = 1, iterations do chk = chk + check(make(d)) end
  print(iterations .. "\t trees of depth " .. d .. "\t check: " .. chk)
end
print("long lived tree of depth " .. maxDepth .. "\t check: " .. check(longLived))

-- resource table の handle → entry の表: 大量の宣言と sweep と表の縮小の
-- 後も、生きている handle が正しい entry を引き、sweep で消えた handle は
-- 引かず、新しい key には前の値と重ならない handle が付く。
local M = {}
local f = 0
local refs = {}
local N = 6000
function M.on_init()
	lub.config({ width = 32, height = 32, resource_sweep_after_frames = 3 })
end
local function fail(msg)
	print("RESOURCE_HANDLES_FAIL frame " .. f .. ": " .. msg)
	os.exit(1, true)
end
function M.on_frame()
	f = f + 1
	if f == 1 then
		for i = 1, N do
			refs[i] = lub.gfx.use_buffer("k" .. i, lub.gfx.STORAGE, { i, 0, 0, 0 }, 1)
		end
	elseif f <= 12 then
		-- 偶数だけ使い続ける (奇数は sweep される)
		for i = 2, N, 2 do
			local r = lub.gfx.use_buffer("k" .. i, lub.gfx.STORAGE, nil, 1)
			if not r or r.handle ~= refs[i].handle then
				fail("re-assert of k" .. i .. " changed handle")
			end
		end
	elseif f == 13 then
		for i = 1, N do
			local ok, key, ver = lub.gfx.resource_info(refs[i].handle)
			if i % 2 == 0 then
				if key ~= "k" .. i or ver ~= 1 then
					fail("live k" .. i .. " resolves to " .. tostring(key))
				end
			elseif ok then
				fail("swept k" .. i .. " still resolves to " .. tostring(key))
			end
		end
		-- 新しい key は新しい handle (前の値と重ならない)
		local seen = {}
		for i = 1, N do
			seen[refs[i].handle] = true
		end
		for i = 1, 3000 do
			local r = lub.gfx.use_buffer("n" .. i, lub.gfx.STORAGE, { 1, 2, 3, 4 }, 1)
			if seen[r.handle] then
				fail("new key reused handle " .. r.handle)
			end
			local _, key = lub.gfx.resource_info(r.handle)
			if key ~= "n" .. i then
				fail("n" .. i .. " resolves to " .. tostring(key))
			end
		end
		print("RESOURCE_HANDLES_OK")
		os.exit(0, true)
	end
end
return M

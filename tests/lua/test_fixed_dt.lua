-- --fixed-dt が指定値をそのまま onFrame(dt) へ渡すことを確認する。
local M = {}

local EXPECTED_DT = tonumber(os.getenv("LUB_EXPECT_FIXED_DT") or "0.0125")

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
end

function M.onFrame(dt)
	if type(dt) ~= "number" or math.abs(dt - EXPECTED_DT) > 1e-15 then
		print("FIXED_DT_SMOKE_FAIL: expected " .. EXPECTED_DT .. ", got " .. tostring(dt))
		os.exit(1, true)
	end
	print("FIXED_DT_SMOKE_OK: dt=" .. dt)
	os.exit(0, true)
end

return M

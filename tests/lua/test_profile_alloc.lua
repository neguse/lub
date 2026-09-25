-- LUB_PROFILE の Lua heap の数字 (確保量と GC)。scripts/native-gate.sh が
-- LUB_PROFILE=1 LUB_PROFILE_START_FRAME=10 で走らせ、終了時の report
-- (label=exit) を読んで突き合わせる。Lua からは profiler の数字を読めない
-- ので、ここでは 1 frame 分のごみの量を GC を止めた collectgarbage("count")
-- の差で測って PROFILE_ALLOC_EXPECT_KB として出す。
--   * frame の確保量と scope test.garbage の確保量がその値に合う
--   * ごみが続くので GC step と GC の周回が数えられている
--   * 値だけを返す binding (scope test.binding) は確保しない
local M = {}

local FRAMES = 70
local TABLES = 2000

local frame = 0

-- 毎回同じ量を確保して捨てる (長さ 1 の配列を持つ table)
local function make_garbage()
	local last
	for i = 1, TABLES do
		last = { i }
	end
	return last
end

local function call_bindings()
	local n = 0
	for _ = 1, 100 do
		if lub.input.key_down("space") or lub.input.mouse_down() then
			n = n + 1
		end
		local w, h = lub.gfx.size()
		local x, y = lub.input.mouse_pos()
		n = n + w + h + x + y + lub.sys.actual_fps()
	end
	return n
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	if not lub.profiler.enabled() then
		print("PROFILE_ALLOC_FAIL: run with LUB_PROFILE=1")
		os.exit(1, true)
	end
	collectgarbage("stop")
	local before = collectgarbage("count")
	make_garbage()
	local kb = collectgarbage("count") - before
	collectgarbage("restart")
	if kb < 16 then
		print("PROFILE_ALLOC_FAIL: garbage too small: " .. kb .. " KB")
		os.exit(1, true)
	end
	print(string.format("PROFILE_ALLOC_EXPECT_KB=%.3f", kb))
end

local function measured_frame()
	lub.profiler.begin_scope("test.garbage")
	make_garbage()
	lub.profiler.end_scope("test.garbage")
	lub.profiler.begin_scope("test.binding")
	call_bindings()
	lub.profiler.end_scope("test.binding")
end

function M.on_frame()
	-- player は on_frame の error を log に出して次の frame へ進むので、数えるのと
	-- 終わるのを先にし、error はその場で失敗にする (待ち続けないように)
	frame = frame + 1
	if frame >= FRAMES then
		lub.quit()
	end
	local ok, err = pcall(measured_frame)
	if not ok then
		print("PROFILE_ALLOC_FAIL: on_frame: " .. tostring(err))
		os.exit(1, true)
	end
end

return M

-- tests/lua/test_resource_handles.lua
-- resource の handle は、key を宣言し続けている間は同じ値で、sweep された後は
-- stale になる (別の key の handle として使い回されない)。key を毎フレーム
-- 作って捨てる書き方でも、生きている key の handle は変わらない。
-- 表の大きさが伸びないことは tests/c/resources_smoke.c が見る。

local M = {}

local SWEEP = 2
local FRAMES = 60
local PER_FRAME = 50
local DATA = { 0.0, 0.0, 0.0 }

local frame = 0
local keep_handle
local issued = {} -- handle -> 宣言した key (全 frame 分)
local history = {} -- frame -> { handle, ... }

local function fail(message)
	print("RESOURCE_HANDLES_FAIL: " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", resource_sweep_after_frames = SWEEP })
end

function M.on_frame()
	frame = frame + 1

	local keep = lub.gfx.use_buffer("handles_keep", lub.gfx.STORAGE, DATA, 1)
	if not keep_handle then
		keep_handle = keep.handle
	elseif keep.handle ~= keep_handle then
		fail("a live key must keep its handle: " .. keep_handle .. " -> " .. keep.handle)
	end

	history[frame] = {}
	for i = 1, PER_FRAME do
		local key = string.format("handles_churn_%d_%d", frame, i)
		local ref = lub.gfx.use_buffer(key, lub.gfx.STORAGE, DATA, 1)
		if issued[ref.handle] then
			fail("handle " .. ref.handle .. " was issued twice (" .. issued[ref.handle] .. ", " .. key .. ")")
		end
		issued[ref.handle] = key
		history[frame][i] = ref.handle
	end

	-- SWEEP + 2 frame より前に宣言した key は sweep 済み: handle は stale
	local old = history[frame - SWEEP - 2]
	if old then
		for _, handle in ipairs(old) do
			if lub.gfx.resource_info(handle) then
				fail("handle " .. handle .. " of a swept key must be stale")
			end
		end
	end
	-- 生きている handle は宣言した key に引ける
	local ok, key = lub.gfx.resource_info(keep_handle)
	if not ok or key ~= "handles_keep" then
		fail("the live handle must resolve to its key: " .. tostring(key))
	end
	for _, handle in ipairs(history[frame]) do
		local live, live_key = lub.gfx.resource_info(handle)
		if not live or live_key ~= issued[handle] then
			fail("handle " .. handle .. " must resolve to " .. issued[handle] .. ", got " .. tostring(live_key))
		end
	end

	if frame >= FRAMES then
		print("RESOURCE_HANDLES_OK frames=" .. frame)
		lub.app.quit()
	end
end

return M

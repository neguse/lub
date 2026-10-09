-- tests/lua/test_arena_error_release.lua
-- 生成 binding の途中で Lua error が出ると、呼び出し用の arena の release を
-- longjmp で飛び越す。runtime は on_frame の前に arena を巻き戻すので、毎 frame
-- 失敗しても arena は伸び続けない。この test は大きい配列を arena に写した
-- 直後に型 error を起こす呼び出しを 200 frame 繰り返し、process が生きて
-- 正常に終わることを確認する (arena は C memory なので RSS は手で測る)。

local M = {}

local FRAMES = 200
local frame = 0
local data = {}
for i = 1, 1 << 20 do
	data[i] = 0.0
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_frame()
	frame = frame + 1
	-- data (4 MiB) を arena に写した後、version の型 error で release を飛び越す
	local ok, err = pcall(lub.gfx.use_buffer, "arena_test", lub.gfx.STORAGE, data, "bad-version")
	assert(not ok, "use_buffer with a wrong-typed version must fail")
	assert(type(err) == "string", "error must be a message")
	-- 失敗の後も正常な呼び出しは通る
	local b = lub.gfx.use_buffer("arena_test", lub.gfx.STORAGE, { 0.0, 0.0, 0.0 })
	assert(b ~= nil, "use_buffer after a failed call must succeed")
	if frame >= FRAMES then
		print("ARENA_ERROR_RELEASE_OK frames=" .. frame)
		lub.app.quit()
	end
end

return M

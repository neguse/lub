-- raw Lua から lubx (C# で書いた実装ライブラリ) を使うサンプル。samples/lubx.lua
-- は cs-lib から tcs が生成した Lua (scripts/gen-lubx-lua.sh) で、require が
-- 型の table を返す。名前は C# の PascalCase を snake_case に写したもの
-- (SpriteBatch.new / batch:rect / Color.rgb)。
local lubx = require("lubx")

local W, H = 640, 480
local M = {}
local batch
local t = 0

function M.on_init()
	lub.config({ width = W, height = H })
end

function M.on_frame(dt)
	t = t + dt
	if batch == nil then
		batch = lubx.SpriteBatch.new(W, H, "spr28_shader", "spr28_batch", true)
	end
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.08, 0.08, 0.12, 1.0 } })
	batch:begin()
	for i = 0, 23 do
		local a = t * 0.7 + i * (math.pi * 2 / 24)
		local x = W / 2 + math.cos(a) * 180
		local y = H / 2 + math.sin(a) * 140
		local hue = i / 24
		local color = lubx.Color.rgb(
			0.5 + 0.5 * math.sin(hue * 6.28),
			0.5 + 0.5 * math.sin(hue * 6.28 + 2.1),
			0.5 + 0.5 * math.sin(hue * 6.28 + 4.2),
			1.0
		)
		if i % 2 == 0 then
			batch:rect(x - 16, y - 16, 32, 32, color)
		else
			batch:disc(x, y, 18, color)
		end
	end
	batch:rect(W / 2 - 60, H / 2 - 8, 120, 16, lubx.Color.rgb(1, 1, 1, 0.8))
	batch:flush()
	lub.gfx.end_pass()
end

return M

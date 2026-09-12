-- 実際に縞が出たコインの平面は、光に向いており単体では遮蔽されない。
-- 影 ON/OFF を微小に移動・回転した 5 姿勢で比較する。旧方式は同じテストに失敗する。
local x = dofile("samples/lubx.lua")
local legacy = os.getenv("LUB_SHADOW_LEGACY") == "1"
if legacy then
	local f = assert(io.open("samples/28_renderer_shadow/coin_shadow_legacy.slang", "r"))
	x.Renderer3d.lit_fs = f:read("*a")
	f:close()
end
local base = x.Mat4.new()
base.m = dofile("samples/28_renderer_shadow/coin_acne_pose.lua")
local r, coin, rb
local frame = 0
local samples, results = {}, {}
local function check(ok, msg)
	if not ok then
		print("COIN_SHADOW_FAIL " .. msg)
		os.exit(1, true)
	end
end
return {
	on_init = function()
		lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan", width = 960, height = 540 })
		r = x.Renderer3d.new("coin_shadow_test")
		coin = x.Mesh3d.new("coin_shadow_mesh")
		rb = lub.gfx.readback("coin_shadow_read")
	end,
	on_frame = function()
		frame = frame + 1
		check(frame < 30, "readback timeout")
		if not coin:ready() then
			coin:rebuild(x.Shapes3d.cylinder(24))
		end
		local phase = math.min(frame, 10)
		local pose = math.floor((phase - 1) / 2) - 2
		local cx, cy, cz = base.m[4], base.m[8], base.m[12]
		local m = x.Mat4
			.translate(x.Vec3.new(cx + pose * 0.001, cy, cz))
			:mul(x.Mat4.rotate_y(pose * 0.005))
			:mul(x.Mat4.translate(x.Vec3.new(-cx, -cy, -cz)))
			:mul(base)
		r.light.dir = x.Vec3.new(-0.25, 1, 0.5)
		r.light.intensity = 1.2
		r.sky.top = x.Color.rgb(0.32, 0.35, 0.44)
		r.sky.bottom = x.Color.rgb(0.1, 0.1, 0.12)
		r.sky.intensity = 0.45
		r.shadow.extent = 3
		r.shadow.enabled = phase % 2 == 1
		if legacy then
			r.shadow.bias = 0.001
		end
		r.ssao.enabled = false
		r.bloom.enabled = false
		r.aa.enabled = false
		r.dither = false
		r:begin({
			eye = x.Vec3.new(-1.1987, 1.8288, 1.6978),
			target = x.Vec3.new(-1.1987, 0.3688, -0.2022),
			fov = 20,
			near = 0.1,
			far = 50,
		})
		r:draw(coin, m, { tint = x.Color.rgb(0.85, 0.68, 0.2) })
		r:end_()
		if frame <= 10 then
			local pts = {}
			for iz = -4, 4 do
				for ix = -4, 4 do
					local u, v = ix * 0.15, iz * 0.15
					if u * u + v * v <= 0.65 * 0.65 then
						local p = m:mul_vec4(x.Vec4.new(u, 0.5, v, 1))
						local q = r.view_proj:mul_vec4(p)
						pts[#pts + 1] =
							{ math.floor((q.x / q.w * 0.5 + 0.5) * 960), math.floor((q.y / q.w * 0.5 + 0.5) * 540) }
					end
				end
			end
			samples[phase] = pts
		end
		local tex = lub.gfx.use_texture(
			"coin_shadow_test_ldr",
			960,
			540,
			lub.gfx.RGBA8,
			nil,
			960 * 65536 + 540,
			{ target = true, filter = lub.gfx.LINEAR, wrap = lub.gfx.CLAMP }
		)
		local status, bytes, w, h, fmt, stride, id = rb:read_texture(tex, frame <= 10 and phase or nil)
		if status == "ready" then
			local values = {}
			for _, p in ipairs(samples[id]) do
				local sum = 0
				for dy = -1, 1 do
					for dx = -1, 1 do
						local k = (p[2] + dy) * stride + (p[1] + dx) * 4
						sum = sum + bytes:get(k) + bytes:get(k + 1) + bytes:get(k + 2)
					end
				end
				values[#values + 1] = sum / 27
			end
			results[id] = values
		end
		if results[10] then
			for i = 1, 9, 2 do
				local largest, total = 0, 0
				for j, a in ipairs(results[i]) do
					local delta = math.abs(a - results[i + 1][j])
					largest = math.max(largest, delta)
					total = total + delta
				end
				local mean = total / #results[i]
				print("COIN_SHADOW_POSE", (i + 1) / 2, "max", largest, "mean", mean)
				check(largest <= 2 and mean <= 0.4, "unoccluded coin cap must match shadows off")
			end
			print("COIN_SHADOW_PASS")
			lub.quit()
		end
	end,
}

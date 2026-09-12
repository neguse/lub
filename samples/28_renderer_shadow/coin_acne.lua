-- 光とほぼ平行になったコイン 1 枚。S: 影、space: 微小な移動と回転。
local x = dofile("samples/lubx.lua")
local mode = os.getenv("COIN_MODE") or "fixed"
if mode == "legacy" then
	local f = assert(io.open("samples/28_renderer_shadow/coin_shadow_legacy.slang", "r"))
	x.Renderer3d.lit_fs = f:read("*a")
	f:close()
end
local base = x.Mat4.new()
base.m = dofile("samples/28_renderer_shadow/coin_acne_pose.lua")
local r, coin
local shadows = mode ~= "off"
local moving, t = false, 0
return {
	on_init = function()
		lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan", width = 960, height = 540 })
		r = x.Renderer3d.new("coin_acne")
		coin = x.Mesh3d.new("coin_acne_mesh")
		print("coin_acne mode=" .. mode .. " (S: shadow, space: motion)")
	end,
	on_frame = function(dt)
		if not coin:ready() then
			coin:rebuild(x.Shapes3d.cylinder(24))
		end
		if lub.input.key_pressed("s") then
			shadows = not shadows
		end
		if lub.input.key_pressed("space") then
			moving = not moving
		end
		if moving then
			t = t + dt
		end
		r.shadow.enabled = shadows
		r.shadow.extent = 3
		if mode == "legacy" then
			r.shadow.bias = 0.001
		end
		r.light.dir = x.Vec3.new(-0.25, 1, 0.5)
		r.light.intensity = 1.2
		r.sky.top = x.Color.rgb(0.32, 0.35, 0.44)
		r.sky.bottom = x.Color.rgb(0.1, 0.1, 0.12)
		r.sky.intensity = 0.45
		r.background = x.Color.rgb(0.035, 0.045, 0.06)
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
		local center = x.Vec3.new(base.m[4], base.m[8], base.m[12])
		local model = x.Mat4
			.translate(x.Vec3.new(center.x + math.sin(t) * 0.006, center.y, center.z))
			:mul(x.Mat4.rotate_y(math.sin(t) * 0.02))
			:mul(x.Mat4.translate(x.Vec3.new(-center.x, -center.y, -center.z)))
			:mul(base)
		r:draw(coin, model, { tint = x.Color.rgb(0.85, 0.68, 0.2) })
		r:end_()
	end,
}

-- 実画素で影の位置、裏面の照明、薄い円柱の接地影、SSAO の向きを検査する。
-- 明るさの絶対値や PNG の一致ではなく、同じ面の対照画像との差を使う。
local x = dofile("samples/lubx.lua")
local V, M, C = x.Vec3, x.Mat4, x.Color
local r, cube, coin, rb
local frame = 0
local results = {}
local function check(ok, message)
	if not ok then
		print("RENDERER_SHADOW_FAIL " .. message)
		os.exit(1, true)
	end
end
local function model(px, py, pz, sx, sy, sz)
	return M.translate(V.new(px, py, pz)) * M.scale(V.new(sx, sy, sz))
end
local function pixel(bytes, stride, wp)
	local q = r.view_proj:mul_vec4(x.Vec4.new(wp[1], wp[2], wp[3], 1))
	-- LDR 中間テクスチャは最終画面と上下が逆。公開の view_proj から射影する。
	local px = math.floor((q.x / q.w * 0.5 + 0.5) * 960)
	local py = math.floor((q.y / q.w * 0.5 + 0.5) * 540)
	local v = 0
	for dy = -1, 1 do
		for dx = -1, 1 do
			local i = (py + dy) * stride + (px + dx) * 4
			v = v + bytes:get(i) + bytes:get(i + 1) + bytes:get(i + 2)
		end
	end
	return v / 27
end
return {
	on_init = function()
		lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan", width = 960, height = 540 })
		r = x.Renderer3d.new("shadow_test")
		cube = x.Mesh3d.new("cube")
		coin = x.Mesh3d.new("coin")
		rb = lub.gfx.readback("shadow_test")
	end,
	on_frame = function()
		frame = frame + 1
		if frame == 1 then
			cube:rebuild(x.Shapes3d.cube())
			coin:rebuild(x.Shapes3d.cylinder(24))
		end
		check(frame < 20, "readback did not complete")
		-- 箱の影 ON/OFF、薄い円柱の影 ON/OFF、接触物あり/なしの SSAO。
		local phase = math.min(frame, 6)
		r.light.dir = V.new(1, 2, 0)
		r.shadow.extent = 3
		r.shadow.bias = phase <= 2 and 0.004 or 0.001
		r.shadow.enabled = phase == 1 or phase == 3
		r.ssao.enabled = phase >= 5
		r.bloom.enabled = false
		r.aa.enabled = false
		r.dither = false
		r:begin({ eye = V.new(-3, 5, -6), target = V.new(-0.4, 0.4, 0), fov = 42, near = 0.1, far = 30 })
		r:draw(cube, model(0, -0.1, 0, 2.5, 0.1, 2), { tint = C.rgb(0.7, 0.7, 0.7) })
		if phase <= 2 then
			r:draw(cube, model(0, 1, 0, 0.5, 0.5, 0.5), { tint = C.rgb(0.7, 0.7, 0.7) })
		elseif phase <= 4 then
			r:draw(coin, model(0, 0.035, 0, 0.4, 0.07, 0.4), { tint = C.rgb(0.7, 0.7, 0.7) })
		elseif phase == 5 then
			r:draw(cube, model(0, 0.5, 0, 0.5, 0.5, 0.5), { tint = C.rgb(0.7, 0.7, 0.7) })
		end
		r:end_()
		local tex = lub.gfx.use_texture(
			"shadow_test_ldr",
			960,
			540,
			lub.gfx.RGBA8,
			nil,
			960 * 65536 + 540,
			{ target = true, filter = lub.gfx.LINEAR, wrap = lub.gfx.CLAMP }
		)
		local st, bytes, w, h, fmt, stride, id = rb:read_texture(tex, frame <= 6 and phase or nil)
		if st == "ready" then
			results[id] = {
				pixel(bytes, stride, { -1, 0, 0 }),
				pixel(bytes, stride, { -0.2, 1, -0.5 }),
				pixel(bytes, stride, { -0.426, 0, 0 }),
				pixel(bytes, stride, { 0, 0, -0.55 }),
				pixel(bytes, stride, { -1.5, 0, 0 }),
			}
			print("PHASE", id, table.unpack(results[id]))
		end
		if results[6] then
			check(results[2][1] - results[1][1] > 30, "box must cast a shadow at its projected position")
			check(math.abs(results[2][5] - results[1][5]) < 1, "floor outside the projection must stay lit")
			check(
				math.abs(results[2][2] - results[1][2]) < 1,
				"back-facing surface must not gain direct light when shadows are off"
			)
			check(results[4][3] - results[3][3] > 15, "0.07-thick coin must cast a contact shadow with bias 0.001")
			check(results[6][4] - results[5][4] > 1, "SSAO must darken the floor beside a touching box")
			check(math.abs(results[6][5] - results[5][5]) < 1, "SSAO must leave the distant floor unchanged")
			print("RENDERER_SHADOW_PASS")
			lub.quit()
		end
	end,
}

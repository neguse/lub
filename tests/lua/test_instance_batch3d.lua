-- lubx.InstanceBatch3d と Renderer3d.DrawInstances:
--   * instance ごとの位置と色で描かれる (色を塗る pass)
--   * 同じ batch が影を落とす (shadow pass)。blend を指定すると影を落とさない
--   * 空の batch は何も描かない (何も描かないフレームと同じ絵)
--   * フレームごとに数が変わっても (1 → 1000 → 10) 正しく描き、前のフレームの
--     残りを描かない
--   * Renderer3d.Draw は渡した行列を写す (1 つの Mat4 を書き換えながら渡しても、
--     描画ごとに別の Mat4 を渡したのと同じ絵になる)
--   * instance の位置と向きは、同じ変換を Mat4 にした Renderer3d.Draw と
--     1 画素以内で一致する (輪郭の画素がどれも相手の輪郭から 1 画素以内にある)
-- 真上から見下ろすカメラで描き、Renderer3d の LDR target (key .. "_ldr") を
-- 読み戻して画素で確かめる。
local x = dofile("samples/lubx.lua")

local W, H = 128, 128
local KEY = "ibt"
local f = 0
local rb
local ren, cube, batch
local step = 1
local requested = nil
local results = {}
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"

local function fail(message)
	print("INSTANCE_BATCH3D_FAIL frame " .. f .. " step " .. step .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

-- 真上 (y = 20) から見下ろす。画面の右が world +X、上が world +Z
local function begin_scene(light_dir)
	ren.background = x.Color.rgb(0, 0, 0)
	ren.ssao.enabled = false
	ren.bloom.enabled = false
	ren.dither = false
	ren.shadow.size = 512
	ren.shadow.extent = 8
	ren.light.dir = light_dir or x.Vec3.new(-0.4, 1.0, -0.55)
	local cam = x.Camera.new()
	cam.eye = x.Vec3.new(0, 20, 0)
	cam.target = x.Vec3.new(0, 0, 0)
	cam.up = x.Vec3.new(0, 0, 1)
	cam.fov = 30
	ren:begin(cam)
end

-- world の点を LDR target の画素に写す。offscreen の target は clip の y を
-- 反転して描かれるので、読み戻した行は clip の y が大きいほど下になる
local function project(px, py, pz)
	local m = ren.view_proj.m
	local cx = m[1] * px + m[2] * py + m[3] * pz + m[4]
	local cy = m[5] * px + m[6] * py + m[7] * pz + m[8]
	local cw = m[13] * px + m[14] * py + m[15] * pz + m[16]
	return (cx / cw * 0.5 + 0.5) * W, (cy / cw * 0.5 + 0.5) * H
end

local function ground()
	-- 上面が y = 0 の床 (Shapes3d.Cube は -1..1)
	local m = x.Mat4.new():set_trs(0, -0.05, 0, 0, 0, 0, 1, 7, 0.05, 7)
	ren:draw(cube, m, nil)
end

local function quat_y(angle)
	return x.Quat.from_axis_angle(x.Vec3.new(0, 1, 0), angle)
end

-- instance の変換 (位置、軸ごとの大きさ、回転) と色
local SHAPES = {
	{ -3.0, 0.0, 1.5, 0.6, 0.4, 0.3, 0.3, { 1.0, 0.1, 0.1, 1 } },
	{ 0.0, 0.5, -2.0, 0.3, 0.8, 0.5, 1.1, { 0.1, 1.0, 0.1, 1 } },
	{ 3.0, 1.0, 1.0, 0.5, 0.5, 0.5, -0.7, { 0.1, 0.1, 1.0, 1 } },
}

local function add_shapes()
	for _, s in ipairs(SHAPES) do
		local q = quat_y(s[7])
		local c = s[8]
		batch:add_scaled(s[1], s[2], s[3], s[4], s[5], s[6], q.x, q.y, q.z, q.w, c[1], c[2], c[3], c[4])
	end
end

-- 1000 個の格子 (40 x 25)。i 番目の中心
local function grid_pos(i)
	local gx = i % 40
	local gz = (i - gx) / 40
	return -4.875 + gx * 0.25, -3.0 + gz * 0.25
end

local function add_grid(n)
	for i = 0, n - 1 do
		local px, pz = grid_pos(i)
		batch:add(px, 0.0, pz, 0.08, 0, 0, 0, 1, 1.0, 0.9, 0.2, 1)
	end
end

local function pixel(img, px, py)
	local ix = math.floor(px)
	local iy = math.floor(py)
	expect(ix >= 0 and ix < W and iy >= 0 and iy < H, "pixel out of range " .. ix .. "," .. iy)
	local o = iy * img.stride + ix * 4
	return img.data:byte(o + 1), img.data:byte(o + 2), img.data:byte(o + 3)
end

local function lit(r, g, b)
	return r + g + b > 30
end

-- (x0, y0)-(x1, y1) の中で背景でない画素の外接矩形
local function bbox(img, x0, y0, x1, y1)
	local minx, miny, maxx, maxy = nil, nil, nil, nil
	for py = y0, y1 do
		for px = x0, x1 do
			if lit(pixel(img, px, py)) then
				minx = math.min(minx or px, px)
				maxx = math.max(maxx or px, px)
				miny = math.min(miny or py, py)
				maxy = math.max(maxy or py, py)
			end
		end
	end
	return minx, miny, maxx, maxy
end

-- 背景でない画素の集まり (添字は iy * W + ix)
local function lit_mask(img)
	local m = {}
	for py = 0, H - 1 do
		for px = 0, W - 1 do
			m[py * W + px] = lit(pixel(img, px, py))
		end
	end
	return m
end

-- a の背景でない画素がどれも、b の背景でない画素から縦横斜め 1 画素以内にある。
-- 外れた最初の画素を返す
local function covered_within_one(a, b)
	for py = 0, H - 1 do
		for px = 0, W - 1 do
			if a[py * W + px] then
				local near = false
				for dy = -1, 1 do
					for dx = -1, 1 do
						local qx, qy = px + dx, py + dy
						if qx >= 0 and qx < W and qy >= 0 and qy < H and b[qy * W + qx] then
							near = true
						end
					end
				end
				if not near then
					return px, py
				end
			end
		end
	end
	return nil
end

local function dominant(img, px, py, channel, what)
	local c = { pixel(img, px, py) }
	local other1 = c[channel % 3 + 1]
	local other2 = c[(channel + 1) % 3 + 1]
	expect(
		c[channel] > other1 + 40 and c[channel] > other2 + 40,
		string.format("%s: pixel (%d, %d) is (%d, %d, %d)", what, math.floor(px), math.floor(py), c[1], c[2], c[3])
	)
end

local function luma(img, px, py)
	local r, g, b = pixel(img, px, py)
	return r + g + b
end

local steps = {
	{
		name = "lit: each instance at its position with its color",
		draw = function()
			begin_scene()
			batch:begin()
			add_shapes()
			expect(batch.count == 3, "count must be 3, got " .. tostring(batch.count))
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			for i, s in ipairs(SHAPES) do
				-- 上面の中心
				local px, py = project(s[1], s[2] + s[5], s[3])
				dominant(img, px, py, i, "instance " .. i)
			end
			-- instance の無い所は背景
			local px, py = project(0, 0, 3.5)
			expect(not lit(pixel(img, px, py)), "empty area must stay background")
		end,
	},
	{
		name = "shadow: the batch casts a shadow",
		draw = function()
			-- 光は +X +Y から。高さ 2 の箱の影は x が 2 だけずれた所 (原点) に落ちる
			begin_scene(x.Vec3.new(1, 1, 0))
			ground()
			batch:begin()
			batch:add(2, 2, 0, 0.5, 0, 0, 0, 1, 1, 1, 1, 1)
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			local sx, sy = project(0, 0, 0)
			local lx, ly = project(-3, 0, -3)
			results.shadowed = luma(img, sx, sy)
			results.lit_ground = luma(img, lx, ly)
			expect(
				results.shadowed + 60 < results.lit_ground,
				"the ground under the instance's shadow must be darker: "
					.. results.shadowed
					.. " vs lit "
					.. results.lit_ground
			)
		end,
	},
	{
		name = "shadow: a blended batch casts no shadow",
		draw = function()
			begin_scene(x.Vec3.new(1, 1, 0))
			ground()
			batch:begin()
			batch:add(2, 2, 0, 0.5, 0, 0, 0, 1, 1, 1, 1, 0.5)
			local opts = x.Draw3dOpts.new()
			opts.blend = lub.gfx.ALPHA
			ren:draw_instances(cube, batch, opts)
			ren:end_()
		end,
		check = function(img)
			local sx, sy = project(0, 0, 0)
			local v = luma(img, sx, sy)
			expect(
				v > results.shadowed + 60,
				"without a shadow the ground must be lit: " .. v .. " vs shadowed " .. results.shadowed
			)
		end,
	},
	{
		name = "nothing drawn (reference for the empty batch)",
		draw = function()
			begin_scene()
			ren:end_()
		end,
		check = function(img)
			results.nothing = img.data
		end,
	},
	{
		name = "empty batch draws nothing",
		draw = function()
			begin_scene()
			batch:begin()
			expect(batch:upload() == nil, "upload of an empty batch must be nil")
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			expect(img.data == results.nothing, "an empty batch must draw nothing")
		end,
	},
	{
		name = "growth: 1 instance",
		draw = function()
			begin_scene()
			batch:begin()
			add_grid(1)
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			local px, pz = grid_pos(0)
			expect(lit(pixel(img, project(px, 0.08, pz))), "instance 0 must be drawn")
			px, pz = grid_pos(1)
			expect(not lit(pixel(img, project(px, 0.08, pz))), "instance 1 must not be drawn")
		end,
	},
	{
		name = "growth: 1000 instances",
		draw = function()
			begin_scene()
			batch:begin()
			add_grid(1000)
			expect(batch.count == 1000, "count must be 1000")
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			for _, i in ipairs({ 0, 9, 10, 500, 777, 999 }) do
				local px, pz = grid_pos(i)
				expect(lit(pixel(img, project(px, 0.08, pz))), "instance " .. i .. " must be drawn")
			end
		end,
	},
	{
		name = "growth: back to 10 instances",
		draw = function()
			begin_scene()
			batch:begin()
			add_grid(10)
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			for i = 0, 9 do
				local px, pz = grid_pos(i)
				expect(lit(pixel(img, project(px, 0.08, pz))), "instance " .. i .. " must be drawn")
			end
			for _, i in ipairs({ 10, 11, 500, 999 }) do
				local px, pz = grid_pos(i)
				expect(
					not lit(pixel(img, project(px, 0.08, pz))),
					"instance " .. i .. " from the previous frame must not be drawn"
				)
			end
		end,
	},
	{
		name = "per-object Draw of the same transforms (reference)",
		draw = function()
			begin_scene()
			for _, s in ipairs(SHAPES) do
				local q = quat_y(s[7])
				local m = x.Mat4.new():set_trs(s[1], s[2], s[3], q.x, q.y, q.z, q.w, s[4], s[5], s[6])
				ren:draw(cube, m, nil)
			end
			ren:end_()
		end,
		check = function(img)
			results.per_object = img
		end,
	},
	{
		name = "Draw copies the model: one Mat4 rewritten for every shape",
		draw = function()
			begin_scene()
			local m = x.Mat4.new()
			for _, s in ipairs(SHAPES) do
				local q = quat_y(s[7])
				m:set_trs(s[1], s[2], s[3], q.x, q.y, q.z, q.w, s[4], s[5], s[6])
				ren:draw(cube, m, nil)
			end
			ren:end_()
		end,
		check = function(img)
			expect(
				img.data == results.per_object.data,
				"drawing with one rewritten Mat4 must match a Mat4 per draw"
			)
		end,
	},
	{
		name = "instances match the per-object Draw within a pixel",
		draw = function()
			begin_scene()
			batch:begin()
			for _, s in ipairs(SHAPES) do
				local q = quat_y(s[7])
				batch:add_scaled(s[1], s[2], s[3], s[4], s[5], s[6], q.x, q.y, q.z, q.w, 1, 1, 1, 1)
			end
			ren:draw_instances(cube, batch, nil)
			ren:end_()
		end,
		check = function(img)
			-- 3 つの箱は画面の左・中・右の帯に分かれている
			local bands = { { 0, 40 }, { 41, 86 }, { 87, W - 1 } }
			for i, band in ipairs(bands) do
				local a = { bbox(results.per_object, band[1], 0, band[2], H - 1) }
				local b = { bbox(img, band[1], 0, band[2], H - 1) }
				expect(a[1] ~= nil and b[1] ~= nil, "box " .. i .. " must be drawn by both")
				for k = 1, 4 do
					expect(
						math.abs(a[k] - b[k]) <= 1,
						string.format(
							"box %d: instanced bounds (%d, %d)-(%d, %d) differ from Draw (%d, %d)-(%d, %d)",
							i,
							b[1],
							b[2],
							b[3],
							b[4],
							a[1],
							a[2],
							a[3],
							a[4]
						)
					)
				end
			end
			-- 外接矩形は回転の向きを逆にしても変わらないので、輪郭の形も比べる
			local a = lit_mask(results.per_object)
			local b = lit_mask(img)
			local px, py = covered_within_one(b, a)
			expect(px == nil, string.format("instanced pixel (%s, %s) is not within a pixel of Draw", px, py))
			px, py = covered_within_one(a, b)
			expect(px == nil, string.format("Draw pixel (%s, %s) is not within a pixel of the instances", px, py))
		end,
	},
}

local function run_step()
	local request = nil
	if not requested then
		steps[step].draw()
		requested = 100 + step
		request = requested
	end
	local tex = lub.gfx.lookup_texture(KEY .. "_ldr")
	expect(tex ~= nil, "the renderer's LDR target must exist")
	local status, bytes, w, h, _, stride, id = rb:read_texture(tex, request)
	expect(status ~= "error", "readback failed")
	if status == "ready" and id == requested then
		expect(w == W and h == H, "unexpected target size " .. w .. "x" .. h)
		steps[step].check({ data = tostring(bytes), stride = stride })
		step = step + 1
		requested = nil
	end
	expect(f < 600, "readback for step " .. step .. " did not arrive")
end

return {
	on_init = function()
		lub.config({ backend = backend, width = W, height = H })
		rb = lub.gfx.readback("ibt_rb")
	end,
	on_frame = function()
		f = f + 1
		if not ren then
			ren = x.Renderer3d.new(KEY)
			cube = x.Mesh3d.new("ibt_cube")
			cube:rebuild(x.Shapes3d.cube())
			batch = x.InstanceBatch3d.new()
		end
		if step <= #steps then
			local ok, err = pcall(run_step)
			if not ok then
				fail(tostring(err))
			end
			return
		end
		print(
			"INSTANCE_BATCH3D_PASS steps="
				.. #steps
				.. " shadowed="
				.. results.shadowed
				.. " lit_ground="
				.. results.lit_ground
		)
		lub.quit()
	end,
}

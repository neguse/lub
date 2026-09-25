-- 使われなくなった resource の sweep (resource_sweep_after_frames = 3)。
--   * 宣言は 1 回でも、毎フレーム draw / dispatch の bindings、shader、pass の
--     color / depth target、read_texture、audio の play に使えば残る
--   * どれにも使わない resource は sweep され、その参照で描くと key を名指す error
--   * 同じ key で宣言し直せば、古い参照も新しい resource に引き直される
--   * on_frame が error で抜けた frame は sweep しない
--   * InstanceCount を 0 で渡した draw は描かない (draw としての検査と使用の記録はする)
local M = {}

local N = 3
local f = 0
local refs = {}
local rb
local rb_ic
local ic = { phase = 0 }

local VS = [[
struct V {
  float2 pos;
};
StructuredBuffer<V> verts;
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.uv = i.pos * 0.5 + 0.5;
  o.pos = float4(i.pos, 0.0, 1.0);
  return o;
}
]]

local FS = [[
LUB_TEXTURE2D(tex);
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return LUB_SAMPLE(tex, i.uv);
}
]]

local QUAD = { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 }

local function fail(message)
	print("RESOURCE_SWEEP_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function white(n)
	local t = {}
	for i = 1, n do
		t[i] = 255
	end
	return t
end

function M.on_init()
	lub.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 64,
		height = 64,
		resource_sweep_after_frames = N,
	})
	rb = lub.gfx.readback("sw_rb")
	rb_ic = lub.gfx.readback("sw_rb_ic")
end

-- 1 回だけ宣言して参照を持つ
local function declare()
	refs.sh = lub.gfx.use_shader("sw_sh", VS, FS, 1)
	refs.drawn = lub.gfx.use_buffer("sw_drawn", lub.gfx.STORAGE, QUAD, 1)
	refs.idle = lub.gfx.use_buffer("sw_idle", lub.gfx.STORAGE, QUAD, 1)
	refs.tex = lub.gfx.use_texture("sw_tex", 2, 2, lub.gfx.RGBA8, white(16), 1)
	refs.rt = lub.gfx.use_texture("sw_rt", 8, 8, lub.gfx.RGBA8, nil, 1, { target = true })
	refs.depth = lub.gfx.use_texture("sw_depth", 8, 8, lub.gfx.DEPTH32F, nil, 1, { target = true })
	refs.read = lub.gfx.use_texture("sw_read", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	local samples = {}
	for i = 1, 480 do
		samples[i] = math.sin(i * 0.05) * 0.1
	end
	refs.snd = lub.audio.snd("sw_snd", samples, 1, 48000, 1)
	local other = {}
	for i = 1, 480 do
		other[i] = math.sin(i * 0.07) * 0.1
	end
	refs.snd_idle = lub.audio.snd("sw_snd_idle", other, 1, 48000, 1)
	-- read_texture だけで使う texture は最初に 1 回だけ描く
	lub.gfx.begin_pass({ target = refs.read, clear_color = { 0, 1, 0, 1 } })
	lub.gfx.end_pass()
end

-- 毎フレームの使用 (宣言はしない)
local function use()
	lub.gfx.begin_pass({ target = refs.rt, depth_target = refs.depth, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.draw(6, { verts = refs.drawn, tex = refs.tex }, { shader = refs.sh, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()
	rb:read_texture(refs.read, f)
	lub.audio.play(refs.snd, { volume = 0 })
end

local function alive(what, h)
	expect(h ~= nil, what .. " must survive while it is used")
end

-- InstanceCount = 0 の draw は描かない: 白い quad の draw を黒の clear の上に
-- 積み、読み戻した画素で見る
local function instance_count_phase()
	local target = lub.gfx.use_texture("sw_ic", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	local count = ic.phase == 1 and 0 or nil
	lub.gfx.begin_pass({ target = target, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.draw(
		6,
		{ verts = refs.drawn, tex = refs.tex },
		{ shader = refs.sh, depth = false, cull = lub.gfx.NONE, instance_count = count }
	)
	lub.gfx.end_pass()
	local request = nil
	if not ic.requested then
		request = 100 + ic.phase
		ic.requested = true
	end
	local status, bytes, _, _, _, _, id = rb_ic:read_texture(target, request)
	if status == "ready" and id == 100 + ic.phase then
		local red = bytes[1]
		if ic.phase == 1 then
			expect(red == 0, "a draw with instance_count = 0 must not draw (red=" .. tostring(red) .. ")")
			ic.phase = 2
			ic.requested = false
		else
			expect(red == 255, "the same draw without instance_count must draw (red=" .. tostring(red) .. ")")
			return true
		end
	end
	expect(f < 200, "readback for the instance_count check did not arrive")
	return false
end

local function frame()
	f = f + 1
	if f == 1 then
		declare()
	end
	if f >= 15 and f <= 24 then
		-- この間の frame は error で抜ける。sweep は止まる
		error("deliberate on_frame failure (sweep must pause)")
	end
	use()

	if f == 12 then
		alive("a drawn buffer", lub.gfx.lookup_buffer("sw_drawn"))
		alive("a bound texture", lub.gfx.lookup_texture("sw_tex"))
		alive("a draw shader", lub.gfx.lookup_shader("sw_sh"))
		alive("a color target", lub.gfx.lookup_texture("sw_rt"))
		alive("a depth target", lub.gfx.lookup_texture("sw_depth"))
		alive("a read_texture source", lub.gfx.lookup_texture("sw_read"))
		expect(lub.audio.snd("sw_snd", nil, 1, 48000, 1) == refs.snd, "a played snd must survive")
		expect(lub.gfx.lookup_buffer("sw_idle") == nil, "an unused buffer must be swept")
		expect(not lub.gfx.resource_info(refs.idle.handle), "the swept handle must be stale")
		expect(lub.audio.snd("sw_snd_idle", nil, 1, 48000, 1) == nil, "an unused snd must be swept")
		-- sweep された参照で描くと key を名指す error
		lub.gfx.begin_pass({ target = refs.rt })
		local ok, err = pcall(
			lub.gfx.draw,
			6,
			{ verts = refs.idle, tex = refs.tex },
			{ shader = refs.sh, depth = false, cull = lub.gfx.NONE }
		)
		lub.gfx.end_pass()
		expect(not ok, "drawing with a swept buffer must raise")
		expect(
			string.find(tostring(err), "'sw_idle'", 1, true) ~= nil,
			"the error must name the key: " .. tostring(err)
		)
		expect(
			string.find(tostring(err), "use_", 1, true) ~= nil,
			"the error must say how to fix it: " .. tostring(err)
		)
	elseif f == 13 then
		-- 宣言し直すと、古い参照は同じ key の新しい resource に引き直される
		local again = lub.gfx.use_buffer("sw_idle", lub.gfx.STORAGE, QUAD, 1)
		expect(again.handle ~= refs.idle.handle, "a swept key must get a new handle")
		lub.gfx.begin_pass({ target = refs.rt })
		local ok, err = pcall(
			lub.gfx.draw,
			6,
			{ verts = refs.idle, tex = refs.tex },
			{ shader = refs.sh, depth = false, cull = lub.gfx.NONE }
		)
		lub.gfx.end_pass()
		expect(ok, "an old reference must resolve to the declared key again: " .. tostring(err))
	elseif f == 14 then
		refs.err = lub.gfx.use_buffer("sw_err", lub.gfx.STORAGE, QUAD, 1)
	elseif f == 25 then
		-- 10 frame の間 error で抜けても、その間は sweep しない
		expect(lub.gfx.lookup_buffer("sw_err") ~= nil, "failed frames must not sweep")
		alive("a drawn buffer after the failed frames", lub.gfx.lookup_buffer("sw_drawn"))
	elseif f == 26 then
		-- error が止んだ frame から sweep は戻る
		expect(lub.gfx.lookup_buffer("sw_err") == nil, "sweep must resume after the failures")
		ic.phase = 1
	elseif f > 26 then
		if instance_count_phase() then
			print("RESOURCE_SWEEP_OK frame=" .. f)
			os.exit(0, true)
		end
	end
end

function M.on_frame()
	local ok, err = pcall(frame)
	if not ok then
		if f >= 15 and f <= 24 and string.find(tostring(err), "deliberate", 1, true) then
			error(err, 0)
		end
		fail(tostring(err))
	end
end

return M

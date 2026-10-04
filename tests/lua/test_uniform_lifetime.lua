local M = {}
local vs = [[
cbuffer Params : register(b0) { float4 rect; float4 shade; };
struct Output { float4 color : COLOR0; float4 position : SV_Position; };
[shader("vertex")]
Output vs_main(uint id : LUB_VERTEX_ID) {
    float2 corners[6] = { float2(0,0), float2(1,0), float2(1,1), float2(0,0), float2(1,1), float2(0,1) };
    float2 p = rect.xy + corners[id] * 4;
    Output o;
    o.color = shade;
    o.position = float4(p.x / 64 - 1, 1 - p.y / 34, 0, 1);
    return o;
}
]]
local fs = [[
struct Input { float4 color : COLOR0; float4 position : SV_Position; };
[shader("fragment")]
float4 fs_main(Input input) : SV_Target { return input.color; }
]]
local readback, frames = nil, 0

function M.on_init()
	lub.config({ width = 128, height = 68 })
	readback = lub.gfx.readback("uniform-lifetime")
end

function M.on_frame()
	local shader = lub.gfx.use_shader("uniform-lifetime", vs, fs, 1)
	if not shader then
		return
	end
	local target = lub.gfx.use_texture("uniform-target", 128, 68, lub.gfx.RGBA8, nil, 1, { target = true })
	lub.gfx.begin_pass({ target = target, clear_color = { 0, 0, 0, 1 } })
	for i = 0, 512 do
		lub.gfx.draw(6, {
			uniforms = {
				rect = { (i % 32) * 4, math.floor(i / 32) * 4, 0, 0 },
				shade = { i % 2, (i % 3) / 2, 1, 1 },
			},
		}, { shader = shader, depth = false, cull = lub.gfx.NONE })
	end
	lub.gfx.end_pass()
	local status, bytes, _, _, _, stride = readback:read_texture(target, frames)
	if status == "ready" then
		for i = 0, 512 do
			local offset = (math.floor(i / 32) * 4 + 2) * stride + ((i % 32) * 4 + 2) * 4
			assert(bytes:get(offset) == (i % 2) * 255, "uniform lifetime: red " .. i)
			assert(math.abs(bytes:get(offset + 1) - (i % 3) * 127.5) <= 0.5, "uniform lifetime: green " .. i)
			assert(bytes:get(offset + 2) == 255, "uniform lifetime: missing draw " .. i)
		end
		frames = frames + 1
		if frames == 3 then
			print("PASS: 513 distinct draw uniforms survive submission and frame reuse")
			lub.quit()
		end
	end
end

return M

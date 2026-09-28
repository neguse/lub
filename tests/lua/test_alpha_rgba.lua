local M = {}
local source = [[
struct Vertex { float4 position : SV_Position; };
[shader("vertex")]
Vertex vs_main(uint id : LUB_VERTEX_ID) {
    Vertex v; v.position = float4(id == 1 ? 3 : -1, id == 2 ? 3 : -1, 0, 1); return v;
}
[shader("fragment")]
float4 fs_main(Vertex input) : SV_Target { return float4(.6, .2, .1, .25); }
]]
function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan" })
end
function M.on_frame()
	local shader = lub.gfx.use_shader("alpha", source, source, 1)
	assert(shader, "alpha blend shader")
	local target = lub.gfx.use_texture("alpha-target", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	for _, case in ipairs({ { lub.gfx.ALPHA, 0.625 }, { lub.gfx.ALPHA_RGBA, 0.4375 } }) do
		lub.gfx.begin_pass({ target = target, clear_color = { 0.1, 0.2, 0.3, 0.5 } })
		lub.gfx.draw(3, {}, { shader = shader, blend = case[1], depth = false, cull = lub.gfx.NONE })
		lub.gfx.end_pass()
		local rb = lub.gfx.readback("alpha" .. case[1])
		rb:read_texture(target, 1)
		local status, bytes = rb:read_texture(target)
		assert(status == "ready", "alpha blend readback")
		for i, value in ipairs({ 0.225, 0.2, 0.25, case[2] }) do
			assert(math.abs(bytes:get(i - 1) - value * 255) < 1.5, "blend channel " .. i)
		end
	end
	print("ALPHA_RGBA_OK")
	lub.quit()
end
return M

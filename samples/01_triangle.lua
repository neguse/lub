local verts = {
   0.0,  0.5, 0.0,
  -0.5, -0.5, 0.0,
   0.5, -0.5, 0.0,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
[shader("vertex")]
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }
]]
local fs = [[
[shader("fragment")]
float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }
]]

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("tri_shader", vs, fs, 1)
  local b = use_buffer("tri_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
  end_pass()
end

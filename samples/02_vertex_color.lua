local verts = {
  -- pos.x, pos.y, pos.z,  color.r, color.g, color.b, color.a
   0.0,  0.5, 0.0,   1, 0, 0, 1,
  -0.5, -0.5, 0.0,   0, 1, 0, 1,
   0.5, -0.5, 0.0,   0, 0, 1, 1,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; };
struct VSOut { float4 pos : SV_Position; float4 color : COLOR; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 1.0);
    o.color = i.color;
    return o;
}
]]
local fs = [[
struct FSIn { float4 color : COLOR; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target { return i.color; }
]]

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("vc_shader", vs, fs, 1)
  local b = use_buffer("vc_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
  end_pass()
end

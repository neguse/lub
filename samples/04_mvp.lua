local verts = {
   0.0,  0.5, 0.0,   1, 0, 0, 1,
  -0.5, -0.5, 0.0,   0, 1, 0, 1,
   0.5, -0.5, 0.0,   0, 0, 1, 1,
}

-- column-major 4x4 (Z-axis rotation)
local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local vs = [[
struct Uniforms { float4x4 mvp; };
ConstantBuffer<Uniforms> u;
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; };
struct VSOut { float4 pos : SV_Position; float4 color : COLOR; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = mul(u.mvp, float4(i.pos, 1.0));
    o.color = i.color;
    return o;
}
]]
local fs = [[
struct FSIn { float4 color : COLOR; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target { return i.color; }
]]

local t = 0
function on_init() end
function on_event(e) end
function on_quit() end

function on_frame()
  t = t + 1/60
  local s = use_shader("mvp_shader", vs, fs, 1)
  local b = use_buffer("mvp_verts", VERTEX, verts, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, uniforms = { mvp = rot_z(t) } },
            { shader = s, depth = false, cull = NONE })
  end_pass()
end

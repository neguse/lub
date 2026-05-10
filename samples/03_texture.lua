local function gen_checker(w, h)
  local out = {}
  for y = 0, h-1 do
    for x = 0, w-1 do
      local c = ((x // 4) + (y // 4)) % 2 == 0
      local v = c and 240 or 60
      local i = (y * w + x) * 4
      out[i+1] = v
      out[i+2] = v
      out[i+3] = v
      out[i+4] = 255
    end
  end
  return out
end

local pixels = gen_checker(32, 32)

local verts = {
  -- pos.xyz, uv.xy
   0.0,  0.7, 0.0,  0.5, 0.0,
  -0.7, -0.7, 0.0,  0.0, 1.0,
   0.7, -0.7, 0.0,  1.0, 1.0,
}

local vs = [[
struct VSIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 1.0);
    o.uv = i.uv;
    return o;
}
]]
local fs = [[
Texture2D    diffuse;
SamplerState diffuse_smp;
struct FSIn { float2 uv : TEXCOORD0; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return diffuse.Sample(diffuse_smp, i.uv);
}
]]

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
  local s = use_shader("tex_shader", vs, fs, 1)
  local b = use_buffer("tex_verts", VERTEX, verts, 1)
  local t = use_texture("tex_chk", 32, 32, RGBA8, pixels, 1)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, diffuse = t }, { shader = s, depth = false, cull = NONE })
  end_pass()
end

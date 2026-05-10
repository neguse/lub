local sg_io = dofile("samples/sg_io.lua")

-- column-major 4x4 (Z-axis rotation)
local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local t = 0
function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
  t = t + 1/60
  local vs, vsv = sg_io.load_text("samples/data/04_mvp.vs.slang")
  local fs, fsv = sg_io.load_text("samples/data/04_mvp.fs.slang")
  local verts, vv = sg_io.load_floats("samples/data/04_mvp.verts.lua")
  if not vs or not fs or not verts then return end
  local s = use_shader("mvp_shader", vs, fs, vsv ~ fsv)
  local b = use_buffer("mvp_verts", VERTEX, verts, vv)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, uniforms = { mvp = rot_z(t) } },
            { shader = s, depth = false, cull = NONE })
  end_pass()
end

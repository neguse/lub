local sg_io = require("sg_io")
local M = {}

-- column-major 4x4 (Z-axis rotation)
local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local t = 0

function M.on_init(self)
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function M.on_event(self, e) end
function M.on_quit(self) end

function M.on_frame(self)
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

return M

local lub_io = require("lub_io")
local M = {}

-- column-major 4x4 (Z-axis rotation)
local function rot_z(theta)
  local c, s = math.cos(theta), math.sin(theta)
  return { c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
end

local t = 0

function M.onInit()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
  t = t + 1/60
  local vs, vsv = lub_io.load_text("samples/data/04_mvp.vs.slang")
  local fs, fsv = lub_io.load_text("samples/data/04_mvp.fs.slang")
  local verts, vv = lub_io.load_floats("samples/data/04_mvp.verts.lua")
  if not vs or not fs or not verts then return end
  local s = use_shader("mvp_shader", vs, fs, vsv ~ fsv)
  local b = use_buffer("mvp_verts", VERTEX, verts, vv)
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
    draw(3, { verts = b, uniforms = { mvp = rot_z(t) } },
            { shader = s, depth = false, cull = NONE })
  end_pass()
end

return M

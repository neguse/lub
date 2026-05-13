local M = {}
local t = 0

function M.on_init()
  local b = os.getenv("SGLUA_BACKEND") or "sokol"
  config({ backend = b })
  print("backend = " .. b)
  print("clear demo")
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
  t = t + 1/60
  local r = 0.5 + 0.5 * math.sin(t)
  local g = 0.5 + 0.5 * math.sin(t + 2.0)
  local b = 0.5 + 0.5 * math.sin(t + 4.0)
  begin_pass({ target = main_tex, clear_color = {r, g, b, 1} })
  end_pass()
end

return M

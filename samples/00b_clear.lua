local M = {}
local t = 0

function M.onInit()
  local b = os.getenv("LUB_BACKEND") or "sokol"
  config({ backend = b })
  print("backend = " .. b)
  print("clear demo")
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
  t = t + 1/60
  local r = 0.5 + 0.5 * math.sin(t)
  local g = 0.5 + 0.5 * math.sin(t + 2.0)
  local b = 0.5 + 0.5 * math.sin(t + 4.0)
  begin_pass({ target = main_tex, clear_color = {r, g, b, 1} })
  end_pass()
end

return M

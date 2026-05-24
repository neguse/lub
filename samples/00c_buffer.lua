local M = {}
local data = { 0, 0.5, 0,  -0.5, -0.5, 0,  0.5, -0.5, 0 }

function M.onInit() end
function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
  local b = use_buffer("tri", VERTEX, data, 1)
  if b and b.__lub_kind == "buffer" then
    -- buffer registered
  end
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end

return M

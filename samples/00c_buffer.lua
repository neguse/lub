local M = {}
local data = { 0, 0.5, 0,  -0.5, -0.5, 0,  0.5, -0.5, 0 }

function M.on_init() end
function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
  local b = use_buffer("tri", VERTEX, data, 1)
  if b and b.__lub_kind == "buffer" then
    -- buffer registered
  end
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end

return M

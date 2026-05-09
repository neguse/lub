local data = { 0, 0.5, 0,  -0.5, -0.5, 0,  0.5, -0.5, 0 }

function on_init() end
function on_event(e) end
function on_quit() end
function on_frame()
  local b = use_buffer("tri", VERTEX, data, 1)
  if b and b.__sgl_kind == "buffer" then
    -- buffer registered
  end
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end

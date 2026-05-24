local M = {}

function M.onInit()
  print("[lua] onInit")
  config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
  print("config called")
  print("VERTEX=", VERTEX, "RGBA8=", RGBA8, "CLEAR=", CLEAR)
  if main_tex and main_tex.__lub_kind == "main_tex" then
    print("main_tex is registered")
  end
end

function M.onEvent(e) end

function M.onFrame()
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end

function M.onQuit()
  print("[lua] onQuit")
end

return M

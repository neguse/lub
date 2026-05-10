function on_init()
  print("[lua] on_init")
  config({ backend = "sokol" })
  print("config called")
  print("VERTEX=", VERTEX, "RGBA8=", RGBA8, "CLEAR=", CLEAR)
  if main_tex and main_tex.__sgl_kind == "main_tex" then
    print("main_tex is registered")
  end
end
function on_event(e) end
function on_frame() end
function on_quit()
  print("[lua] on_quit")
end

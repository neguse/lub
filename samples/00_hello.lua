function on_init()
  print("[lua] on_init")
  config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
  print("config called")
  print("VERTEX=", VERTEX, "RGBA8=", RGBA8, "CLEAR=", CLEAR)
  if main_tex and main_tex.__sgl_kind == "main_tex" then
    print("main_tex is registered")
  end
end
function on_event(e) end
function on_frame()
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end
function on_quit()
  print("[lua] on_quit")
end

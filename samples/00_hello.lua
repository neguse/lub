function on_init()
  print("[lua] on_init")
end
function on_event(e)
  -- noisy なので有効化は必要時に
end
function on_frame()
  -- frame counter があれば 1 秒ごとに print できる
end
function on_quit()
  print("[lua] on_quit")
end

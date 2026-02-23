-- host.lua
-- Simulates lub3d's boot.lua + frame loop pattern.
-- Watches the transpiled .lua file and hot-reloads on change.
--
-- Usage:
--   Terminal 1: dotnet run --project Transpiler -- demos/hotreload/game.cs -o demos/hotreload/game.lua --watch
--   Terminal 2: deps/lua/lua demos/hotreload/host.lua
--   Edit game.cs → tcs re-transpiles → host picks up changes next frame

-- Load TinyC# runtime
local script_dir = arg[0]:match("(.*[/\\])") or "./"
local root_dir = script_dir .. "../../"
local TinySystem = dofile(root_dir .. "runtime/tinysystem.lua")
local HotReload = TinySystem.HotReload

-- Configuration
local GAME_LUA = script_dir .. "game.lua"
local FPS = 5              -- simulated frame rate
HotReload.interval = 0.2   -- check every 200ms

-- State (lives here in host, survives reload — like lub3d's self.xxx)
local state = {
  frame = 0,
}

-- Initial load
print("=== TinyC# Hot Reload PoC ===")
print("Watching: " .. GAME_LUA)
print("Edit game.cs while this is running. Ctrl+C to stop.")
print("")

local ok, err = pcall(dofile, GAME_LUA)
if not ok then
  print("Error loading " .. GAME_LUA .. ": " .. tostring(err))
  print("Run tcs first:")
  print("  dotnet run --project Transpiler -- demos/hotreload/game.cs -o demos/hotreload/game.lua")
  os.exit(1)
end

-- Watch the transpiled file, with on_reload callback
HotReload.watch(GAME_LUA, function()
  if Game and type(Game.OnReload) == "function" then
    pcall(Game.OnReload)
  end
end)

-- Frame loop (simulates lub3d's sokol app loop)
while true do
  -- Safe reload point: start of frame, before game logic
  -- (same as lub3d's boot.lua: desc.frame calls hotreload.update() first)
  HotReload.update()

  -- Game logic
  state.frame = state.frame + 1
  if Game and type(Game.Update) == "function" then
    local ok2, result = pcall(Game.Update, state.frame)
    if ok2 then
      print(result)
    else
      print("[error] " .. tostring(result))
    end
  end

  -- Wait for next frame
  local delay = 1.0 / FPS
  os.execute(string.format("sleep %.2f 2>/dev/null || ping -n 1 -w %d 127.0.0.1 >nul 2>&1", delay, math.floor(delay * 1000)))
end

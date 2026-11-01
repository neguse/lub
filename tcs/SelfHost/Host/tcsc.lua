-- tcs を Lua で動かす host。tcs.lua は SelfHost/Compiler、SelfHost/CBackend、SelfHost/Tool を Lua にしたもの (runtime の前置きを含む)。
-- usage: lua tcsc.lua <tcs.lua> <tcs の引数>...
-- CLI の本体は SelfHost/Tool/Cli.cs で、この host は SelfHost/Tool/TcsHost.cs の宣言を global の tcshost として実装し、
-- Cli.Start を 1 回、tcshost.set_done まで Cli.Step を呼ぶ。
-- tcs と一緒に配るファイル (runtime/*.lua、lib/TinySystem/*.cs) は環境変数 TCS_HOME の dir か、tcs.lua の dir から読む。
-- 素の Lua には stat が無いので、ファイルの同一性は調べない ("-")。変わったかの印はファイルの内容そのもので比べる。
-- 待つ関数は host が global の tcsc_sleep(ms) で用意できる (無ければ os.clock で待つ)。
local toolPath = arg[1]
if not toolPath then
  io.stderr:write("usage: lua tcsc.lua <tcs.lua> <tcs arguments>...\n")
  os.exit(2)
end

local function readAll(path)
  local f = io.open(path, "rb")
  if not f then return nil end
  local text = f:read("a")
  f:close()
  if text == nil then return nil end
  -- UTF-8 の BOM は .NET の File.ReadAllText と同じく除く
  if text:sub(1, 3) == "\239\187\191" then text = text:sub(4) end
  return text
end

local function home()
  local env = os.getenv("TCS_HOME")
  if env and env ~= "" then
    if env:sub(-1) ~= "/" then env = env .. "/" end
    return env
  end
  return toolPath:match("^(.*[/\\])") or "./"
end

local windows = package.config:sub(1, 1) == "\\"

-- 絶対 path にして `.` と `..` と重なった区切りを除く
local function fullPath(path)
  local absolute = path:sub(1, 1) == "/" or path:match("^%a:[/\\]") ~= nil
  if not absolute then
    local cwd = os.getenv("PWD") or (windows and os.getenv("CD")) or "."
    path = cwd .. "/" .. path
  end
  local prefix = ""
  local rest = path
  local drive = path:match("^(%a:)")
  if drive then
    prefix = drive
    rest = path:sub(3)
  end
  local parts = {}
  for part in rest:gmatch("[^/\\]+") do
    if part == ".." then
      if #parts > 0 then parts[#parts] = nil end
    elseif part ~= "." then
      parts[#parts + 1] = part
    end
  end
  local sep = windows and "\\" or "/"
  return prefix .. sep .. table.concat(parts, sep)
end

local exitCode, done = 0, false

tcshost = {
  arg_count = function() return #arg - 1 end,
  arg = function(i) return arg[i + 2] end,
  file_exists = function(path)
    local f = io.open(path, "rb")
    if not f then return false end
    local ok = f:read(0) ~= nil or f:seek("end") == 0
    f:close()
    return ok
  end,
  read_file = function(path) return readAll(path) or "" end,
  read_stdin = function() return io.read("a") or "" end,
  -- 一時ファイルに書いてから置き換える (書きかけの出力を読ませない)
  write_file_atomic = function(path, text)
    local tmp = path .. ".tmp"
    local f, err = io.open(tmp, "wb")
    if not f then return "cannot write '" .. path .. "': " .. tostring(err) end
    f:write(text)
    f:close()
    if windows then os.remove(path) end
    local ok, err2 = os.rename(tmp, path)
    if not ok then
      os.remove(tmp)
      return "cannot write '" .. path .. "': " .. tostring(err2)
    end
    return ""
  end,
  write_out = function(text) io.stdout:write(text) end,
  write_err = function(text) io.stderr:write(text) end,
  flush_out = function() io.stdout:flush() end,
  env = function(name) return os.getenv(name) or "" end,
  path_key = function(path)
    local full = fullPath(path)
    if windows then full = full:lower() end
    return full
  end,
  file_identity = function(path) return "-" end,
  file_stamp = function(path)
    local text = readAll(path)
    if text == nil then return "" end
    return "c" .. text
  end,
  tool_file = function(name) return readAll(home() .. name) or "" end,
  clock = function() return os.date("%H:%M:%S") end,
  sleep = function(ms)
    if tcsc_sleep then
      tcsc_sleep(ms)
    else
      local t = os.clock() + ms / 1000
      repeat until os.clock() >= t
    end
  end,
  set_exit_code = function(code) exitCode = code end,
  set_done = function() done = true end,
}

-- tcs は自分の VM (この host の state) で動く。生成した Lua はファイルに書くだけで実行しない
dofile(toolPath)
local Cli = TcsSelf_Cli
Cli.Start()
while not done do Cli.Step() end
io.stdout:flush()
os.exit(exitCode)

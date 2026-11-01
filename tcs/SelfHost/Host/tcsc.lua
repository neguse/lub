-- セルフホストの tcs compiler を Lua で動かす driver。参照実装の CLI (tcs) のうち、host (lub) が使う形を受ける。
-- usage: lua32 tcsc.lua <compiler.lua> <prelude.lua> [check] [-o out.lua] [--entry Class] [--ref file.cs]... [--lib TinySystem/Linq.cs]...
--          [--watch] [--reload-chunks] [--warnaserror] [--no-runtime] [--parse-only] [--time] files.cs...
-- compiler.lua は TcsSelf の型を定義する chunk (参照実装か、このセルフホストの compiler 自身が作る)。
-- prelude.lua は runtime の前置き (SelfHost/build.sh が参照実装から作る)。出力は prelude + 変換結果。
-- check: 出力を書かず、誤りか準拠の警告 (TCS1001〜1003) があれば終了コード 1 (参照実装の tcs check と同じ)。
-- --watch: 入力を見張り、変わったら変換し直して -o を書き直し、標準出力に
--   `@@tcs_restart_required <byte 数>` の行と理由の行を書く (host は出力から VM を作り直す)。
--   セルフホストは reload chunk を作らないので、--reload-chunks は受けるだけで、変更はいつも作り直しになる。
--   host は global の tcsc_sleep(ms) を用意できる (無ければ os.clock で待つ)。
local compilerPath, preludePath = arg[1], arg[2]
if not compilerPath or not preludePath then
  io.stderr:write("usage: tcsc.lua <compiler.lua> <prelude.lua> [check] [-o out.lua] [--entry Class] [--ref file.cs]... files.cs...\n")
  os.exit(2)
end

local function readFile(path, soft)
  local f, err = io.open(path, "rb")
  if not f then
    if soft then return nil end
    io.stderr:write("error: cannot read " .. path .. ": " .. tostring(err) .. "\n")
    os.exit(2)
  end
  local text = f:read("a")
  f:close()
  return text
end

local out, entry = nil, ""
local noRuntime, parseOnly, timing, check, watch, warnAsError = false, false, false, false, false, false
local paths, refPaths, libPaths = {}, {}, {}
local i = 3
while i <= #arg do
  local a = arg[i]
  if a == "-o" then
    out = arg[i + 1]; i = i + 1
  elseif a == "--entry" then
    entry = arg[i + 1]; i = i + 1
  elseif a == "--ref" then
    refPaths[#refPaths + 1] = arg[i + 1]; i = i + 1
  elseif a == "--lib" then
    libPaths[#libPaths + 1] = arg[i + 1]; i = i + 1
  elseif a == "check" and #paths == 0 then
    check = true
  elseif a == "--watch" then
    watch = true
  elseif a == "--reload-chunks" then
    -- reload chunk は作らない (変更は作り直しで伝える)
  elseif a == "--warnaserror" then
    warnAsError = true
  elseif a == "--no-runtime" then
    noRuntime = true
  elseif a == "--parse-only" then
    parseOnly = true
  elseif a == "--time" then
    timing = true
  else
    paths[#paths + 1] = a
  end
  i = i + 1
end

-- compiler は自分の VM (この driver の state) で動く。生成した Lua はファイルに書くだけで実行しない
local Compiler = dofile(compilerPath)
local Session = TcsSelf_Session

if parseOnly then
  local texts = {}
  for k, p in ipairs(paths) do texts[k] = readFile(p) end
  local t0 = os.clock()
  local diags = Compiler.ParseOnly(paths, texts)
  for _, d in ipairs(diags) do io.stderr:write(d, "\n") end
  local bytes = 0
  for _, t in ipairs(texts) do bytes = bytes + #t end
  local dt = os.clock() - t0
  io.stderr:write(string.format("parsed %d bytes in %.3f s (%.2f MB/s)\n", bytes, dt, bytes / dt / 1e6))
  os.exit(#diags == 0 and 0 or 1)
end

local session = Session.new(entry)
local known = {}

-- 入力を読み、内容が変わったものだけ session に渡す。変わったものがあれば true
local function sync()
  local changed = false
  local function feed(list, method)
    for _, p in ipairs(list) do
      local text = readFile(p, watch)
      if text ~= nil and known[p] ~= text then
        known[p] = text
        session[method](session, p, text)
        changed = true
      end
    end
  end
  feed(paths, "SetSource")
  feed(refPaths, "SetReference")
  feed(libPaths, "SetLibrary")
  return changed
end

local prelude = nil
local function writeOutput(lua)
  local text = lua
  if not noRuntime then
    prelude = prelude or readFile(preludePath)
    text = prelude .. lua
  end
  if out then
    -- 書きかけの出力を host に読ませないよう、別のファイルに書いてから置き換える
    local tmp = out .. ".tmp"
    local f = assert(io.open(tmp, "wb"))
    f:write(text)
    f:close()
    os.remove(out)
    assert(os.rename(tmp, out))
  else
    io.write(text)
  end
end

-- 1 回変換する。診断を stderr に書き、成功なら出力を書いて true
local function build()
  local t0 = os.clock()
  local result = session:Compile()
  if timing then io.stderr:write(string.format("compiled in %.3f s\n", os.clock() - t0)) end
  local failed = not result.Ok
  for _, d in ipairs(result.Diagnostics) do
    io.stderr:write(d, "\n")
    if (check or warnAsError) and string.find(d, " warning ", 1, true) then failed = true end
  end
  if failed then return false end
  if not check then writeOutput(result.Lua) end
  return true
end

sync()
local ok = build()
if not watch then os.exit(ok and 0 or 1) end

local function sleep(ms)
  if tcsc_sleep then
    tcsc_sleep(ms)
  else
    local t = os.clock() + ms / 1000
    repeat until os.clock() >= t
  end
end

local function hostMessage(kind, body)
  io.stdout:write("@@tcs_" .. kind .. " " .. #body .. "\n" .. body)
  io.stdout:flush()
end

io.stderr:write("[tcsc] watching " .. #paths .. " files\n")
while true do
  sleep(250)
  if sync() then
    if build() then
      io.stderr:write("[tcsc] rebuilt (parsed " .. session.ParsedFiles .. " files)\n")
      hostMessage("restart_required", "the self-hosted compiler rebuilds the whole program (no reload chunks)\n")
    end
  end
end

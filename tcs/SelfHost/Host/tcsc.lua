-- セルフホストの tcs compiler を Lua で動かす driver。
-- usage: lua32 tcsc.lua <compiler.lua> <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... [--no-runtime] [--parse-only] files.cs...
-- compiler.lua は TcsSelf.Compiler を return する chunk (参照実装か、このセルフホストの compiler 自身が作る)。
-- prelude.lua は runtime の前置き (gen.sh が参照実装から作る)。出力は prelude + 変換結果。
local compilerPath, preludePath = arg[1], arg[2]
if not compilerPath or not preludePath then
  io.stderr:write("usage: tcsc.lua <compiler.lua> <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... files.cs...\n")
  os.exit(2)
end

local function readFile(path)
  local f, err = io.open(path, "rb")
  if not f then
    io.stderr:write("error: cannot read " .. path .. ": " .. tostring(err) .. "\n")
    os.exit(2)
  end
  local text = f:read("a")
  f:close()
  return text
end

local out, entry, noRuntime, parseOnly, timing = nil, "", false, false, false
local paths, texts, refPaths, refTexts = {}, {}, {}, {}
local i = 3
while i <= #arg do
  local a = arg[i]
  if a == "-o" then
    out = arg[i + 1]; i = i + 1
  elseif a == "--entry" then
    entry = arg[i + 1]; i = i + 1
  elseif a == "--ref" then
    refPaths[#refPaths + 1] = arg[i + 1]
    refTexts[#refTexts + 1] = readFile(arg[i + 1])
    i = i + 1
  elseif a == "--no-runtime" then
    noRuntime = true
  elseif a == "--parse-only" then
    parseOnly = true
  elseif a == "--time" then
    timing = true
  else
    paths[#paths + 1] = a
    texts[#texts + 1] = readFile(a)
  end
  i = i + 1
end

-- compiler は自分の VM (この driver の state) で動く。生成した Lua はファイルに書くだけで実行しない
local Compiler = dofile(compilerPath)
local t0 = os.clock()
if parseOnly then
  local diags = Compiler.ParseOnly(paths, texts)
  for _, d in ipairs(diags) do io.stderr:write(d, "\n") end
  local bytes = 0
  for _, t in ipairs(texts) do bytes = bytes + #t end
  local dt = os.clock() - t0
  io.stderr:write(string.format("parsed %d bytes in %.3f s (%.2f MB/s)\n", bytes, dt, bytes / dt / 1e6))
  os.exit(#diags == 0 and 0 or 1)
end
local result = Compiler.Compile(paths, texts, refPaths, refTexts, entry)
if timing then io.stderr:write(string.format("compiled in %.3f s\n", os.clock() - t0)) end
for _, d in ipairs(result.Diagnostics) do io.stderr:write(d, "\n") end
if not result.Ok then os.exit(1) end
local text = result.Lua
if not noRuntime then text = readFile(preludePath) .. text end
if out then
  local f = assert(io.open(out, "wb"))
  f:write(text)
  f:close()
else
  io.write(text)
end

-- セルフホストの compiler の増分の session の時間を測る。
-- usage: lua32 bench-session.lua <compiler.lua> files.cs...
-- 全体の compile (cold)、何も変えない compile、1 ファイルの本体の編集の後の compile を測る。
-- 本体の編集は、最初のファイルの最後の } の前に空の static method を足す (宣言の形は変わるが parse し直すのはそのファイルだけ)
local compilerPath = arg[1]
if not compilerPath or #arg < 2 then
  io.stderr:write("usage: bench-session.lua <compiler.lua> files.cs...\n")
  os.exit(2)
end

local function readFile(path)
  local f = assert(io.open(path, "rb"))
  local text = f:read("a")
  f:close()
  return text
end

dofile(compilerPath)
local Session = assert(TcsSelf_Session, "the compiler chunk does not define TcsSelf_Session")

local paths, texts = {}, {}
for i = 2, #arg do
  paths[#paths + 1] = arg[i]
  texts[#texts + 1] = readFile(arg[i])
end

local session = Session.new("")
for i = 1, #paths do session:SetSource(paths[i], texts[i]) end

local function timed(label)
  local t0 = os.clock()
  local result = session:Compile()
  local dt = os.clock() - t0
  if not result.Ok then
    for _, d in ipairs(result.Diagnostics) do io.stderr:write(d, "\n") end
    os.exit(1)
  end
  print(string.format("%-28s %7.3f s  (parsed %d files, %d bytes of Lua)", label, dt, session.ParsedFiles, #result.Lua))
  return result
end

local cold = timed("cold")
local again = timed("no change")
assert(again.Lua == cold.Lua, "recompiling without a change changed the output")
local edited = texts[1]:gsub("}%s*$", "    public static void BenchEditMarker() { }\n}\n")
session:SetSource(paths[1], edited)
timed("edit " .. paths[1]:match("[^/]*$"))

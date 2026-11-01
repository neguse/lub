-- セルフホストの tcs compiler の web 用の glue (tcsc_web.c が読む)。
-- compiler (stage2.lua) を読んだ state の上で、TcsSelf_Session を 1 つ持ち、JS からの呼び出しを JSON の文字列で返す。
local Session = assert(TcsSelf_Session, "compiler chunk did not define TcsSelf_Session")
local session = nil

local function jstr(s)
  return '"' .. string.gsub(s, '[%c"\\]', function(c)
    if c == '"' then return '\\"' end
    if c == "\\" then return "\\\\" end
    if c == "\n" then return "\\n" end
    if c == "\r" then return "\\r" end
    if c == "\t" then return "\\t" end
    return string.format("\\u%04x", string.byte(c))
  end) .. '"'
end

local function jlist(list)
  local parts = {}
  for i = 1, #list do parts[i] = jstr(list[i]) end
  return "[" .. table.concat(parts, ",") .. "]"
end

function tcsc_error_json(err)
  return '{"ok":false,"error":' .. jstr(tostring(err)) .. "}"
end

-- 新しい session (entry は出力の最後に return する型の名前)
function tcsc_open(entry)
  session = Session.new(entry or "")
  return '{"ok":true}'
end

function tcsc_set_source(path, text) session:SetSource(path, text); return "" end
function tcsc_set_reference(path, text) session:SetReference(path, text); return "" end
function tcsc_set_library(path, text) session:SetLibrary(path, text); return "" end
function tcsc_remove(path) session:Remove(path); return "" end

-- compile して {ok, lua, errors, warnings, parsed, revision} を返す (lua は runtime の前置きを含まない)
function tcsc_compile()
  local result = session:Compile()
  local errors, warnings = {}, {}
  for _, d in ipairs(result.Diagnostics) do
    if string.find(d, " error ", 1, true) then errors[#errors + 1] = d else warnings[#warnings + 1] = d end
  end
  return '{"ok":' .. tostring(result.Ok)
    .. ',"lua":' .. jstr(result.Lua)
    .. ',"errors":' .. jlist(errors)
    .. ',"warnings":' .. jlist(warnings)
    .. ',"parsed":' .. session.ParsedFiles
    .. ',"revision":' .. session.Revision .. "}"
end

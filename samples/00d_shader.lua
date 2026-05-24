local M = {}

local vs = [[
struct VSIn  { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
[shader("vertex")]
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }
]]
local fs = [[
[shader("fragment")]
float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }
]]

local printed = false

function M.onInit() end
function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
  local s = use_shader("test", vs, fs, 1)
  if not printed and s and s.__lub_kind == "shader" then
    print("shader compiled:", s.key)
    printed = true
  end
  begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
  end_pass()
end

return M

-- 終了 code を受け取れない host (iOS simulator の simctl launch) でテストを
-- 動かすための包み。env LUB_TEST の module をそのまま entry として動かし、
-- 終わり方を "LUB_TEST_EXIT <code>" の 1 行で標準出力に残す。
--   LUB_TEST=tests/lua/test_audio.lua lub tests/lua/run_marked.lua
local M = dofile(assert(os.getenv("LUB_TEST"), "LUB_TEST is not set"))

local exit, quit = os.exit, lub.quit
local function finish(code)
	print("LUB_TEST_EXIT " .. code)
	io.stdout:flush()
end

os.exit = function(code, ...)
	finish((code == nil or code == true) and 0 or code == false and 1 or code)
	exit(code, ...)
end
lub.quit = function()
	finish(0)
	quit()
end

-- callback の error は runtime が log に出して次の frame へ進む。待ち続けない
-- ように、ここで失敗として終える。
for _, name in ipairs({ "on_init", "on_frame", "on_event" }) do
	local callback = M[name]
	if callback then
		M[name] = function(...)
			local ok, err = pcall(callback, ...)
			if not ok then
				io.stderr:write(tostring(err) .. "\n")
				os.exit(1)
			end
		end
	end
end

return M

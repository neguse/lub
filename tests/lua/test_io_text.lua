local M = {}

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan" })
end

local function verify()
	local root = os.tmpname()
	os.remove(root)
	local path = root .. "/nested/scores.txt"
	local original = "得点: 123\n\0end"
	lub.io.save_text(path, original)
	local text, version, status = lub.io.load_text(path)
	assert(status == "ready" and text == original)
	lub.io.save_text(path, "replacement")
	local replaced, revision, ready = lub.io.load_text(path)
	assert(ready == "ready" and replaced == "replacement" and revision ~= version)
	lub.io.save_text(path, "replacement")
	local same, same_revision = lub.io.load_text(path)
	assert(same == "replacement" and same_revision == revision)
	assert(not pcall(lub.io.save_text, root .. "/nested", "cannot replace a directory"))
	assert(lub.io.load_text(path) == "replacement")
	assert(not pcall(lub.io.save_text, path .. "\0suffix", "invalid"))
	lub.io.save_text(path, "")
	local empty, _, empty_status = lub.io.load_text(path)
	assert(empty_status == "ready" and empty == "")
	assert(os.remove(path))
	assert(os.remove(root .. "/nested"))
	assert(os.remove(root))
	print("IO_TEXT_OK")
end

function M.on_frame()
	local ok, err = pcall(verify)
	if not ok then
		io.stderr:write(err .. "\n")
		os.exit(1)
	end
	lub.quit()
end

return M

local subject = require("test_resource_revision_subject")
local first = subject.revision

local swapped, err = lume.hotswap("test_resource_revision_subject")
assert(swapped, err)
assert(swapped == subject, "hotswap must preserve module identity")
assert(subject.revision > first, "resource revision must survive hotswap")

print(string.format("RESOURCE_REVISION_OK before=%d after=%d", first, subject.revision))

local M = {}

function M.onInit()
	Lub.quit()
end

return M

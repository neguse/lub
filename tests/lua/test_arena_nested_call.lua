-- tests/lua/test_arena_nested_call.lua
-- 生成 binding の arena は、大きな確保で chunk (1 MiB) を移った後でも、入れ子の
-- binding 呼び出しの release が外側の使用中の memory を巻き戻さない。
-- mesh の table の __index から別の binding を呼ぶと入れ子になる。
-- positions (12 KB) を読んだ後に normals (約 1 MB) を読むと次の chunk に移り、
-- indices を読むときの __index で内側の呼び出しが走る。外側が次に読む uvs が
-- normals を上書きしていれば、出力の normal に uvs の値が混ざる。

local M = {}

local VERTS = 1000
local NORMAL_FLOATS = 260144 -- positions と合わせて 1 MiB の chunk に収まらない大きさ
local NORMAL = 0.25
local UV = 777.0
local STRIDE = 8 -- PN: position 3 + pad 1 + normal 3 + pad 1

local function fail(message)
	print("ARENA_NESTED_CALL_FAIL: " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_frame()
	local positions, normals, uvs = {}, {}, {}
	for i = 1, VERTS * 3 do
		positions[i] = 0.0
	end
	for i = 1, NORMAL_FLOATS do
		normals[i] = NORMAL
	end
	for i = 1, VERTS * 2 do
		uvs[i] = UV
	end

	local inner_calls = 0
	local mesh = setmetatable({
		positions = positions,
		normals = normals,
		uvs = uvs,
		vert_count = VERTS,
	}, {
		__index = function(_, key)
			if key == "indices" then
				inner_calls = inner_calls + 1
				lub.io.interleave_pn({ positions = { 0, 0, 0 }, normals = { 0, 0, 1 }, vert_count = 1 })
			end
			return nil
		end,
	})

	local out = lub.io.interleave_pn(mesh)
	if inner_calls == 0 then
		fail("the nested binding call must run")
	end
	if #out ~= VERTS * STRIDE then
		fail("unexpected output size " .. #out)
	end
	local corrupted = 0
	for v = 0, VERTS - 1 do
		for k = 5, 7 do
			if out[v * STRIDE + k] ~= NORMAL then
				corrupted = corrupted + 1
			end
		end
	end
	if corrupted ~= 0 then
		fail("corrupted_normal_floats=" .. corrupted)
	end
	print("ARENA_NESTED_CALL_OK")
	lub.app.quit()
end

return M

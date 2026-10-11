-- bit mask の文字列は 1〜16 桁の hex (0x 付きも可)。空、17 桁以上、hex でない
-- 文字 (NUL を含む) は error で、数値など文字列でない値も error (tcs2c と .NET の
-- binding も同じ規則)。

local function fail(message)
	print("BIT_MASK_FAIL " .. message)
	os.exit(1, true)
end

local function check()
	local world = lub.phys2d.world("bits", { gravity = { x = 0, y = 0 } })
	world:begin()
	world:body("box", { type = lub.phys2d.STATIC, initial = { x = 1, y = 0 } }):box("box", { hx = 0.2, hy = 0.2 })
	world:step(1 / 60)
	local function ray(mask)
		return world:raycast({ x = 0, y = 0, dx = 2, dy = 0, filter = { mask_bits = mask } })
	end

	if not ray("1") or not ray("0x1") or not ray("ffffffffffffffff") then
		fail("valid mask missed the box")
	end
	if ray("0") or ray("0x0000000000000000") then
		fail("mask 0 hit the box")
	end
	for _, case in ipairs({
		{ "", "empty" },
		{ "0x", "empty" },
		{ "1ffffffffffffffff", "16 hex digits" },
		{ "0x00000000000000001", "16 hex digits" },
		{ "12g", "invalid hex" },
		{ "1\0zz", "invalid hex" },
		{ "1\0", "invalid hex" },
		{ 1, "must be a hex string (got number)" },
		{ 1.5, "must be a hex string (got number)" },
		{ -1, "must be a hex string (got number)" },
		{ true, "must be a hex string (got boolean)" },
	}) do
		local ok, err = pcall(ray, case[1])
		if ok or not tostring(err):find(case[2], 1, true) then
			fail(string.format("mask %q: want error with %q, got %s", case[1], case[2], tostring(err)))
		end
	end
end

return {
	OnInit = function()
		lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	end,
	OnFrame = function()
		check()
		print("BIT_MASK_PASS")
		os.exit(0, true)
	end,
}

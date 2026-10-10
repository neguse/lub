-- math.* と ^ の結果が OS をまたいでビット単位で同じことを検査する。
-- 決まった入力列の結果の bit 列を関数ごとに hash し、期待値と比べる。期待値は 1 つで、OS ごとに分けない。
-- tcs2c の経路は tests/c/tcs_math.cs が同じ入力列と hash で C# の Math を計算し、scripts/verify-tcs-binding.sh がこの表と比べる。

local expected = {
	sin = -2045485879,
	cos = 1865121772,
	tan = 1184300740,
	asin = -688693406,
	acos = 1881057146,
	atan = 995986894,
	atan2 = -1928894225,
	exp = 153003627,
	log = -625845371,
	log2 = 146272290,
	log10 = 306092410,
	logb = 1222978674,
	pow = -16876670,
	pow2 = -884350922,
}

local POINTS = 2000

-- 1 引数の関数は a の範囲、2 引数の関数は a と b の範囲を並べる
local cases = {
	{ "sin", math.sin, { { -4.0, 4.0 }, { -1e4, 1e4 }, { -1e30, 1e30 } } },
	{ "cos", math.cos, { { -4.0, 4.0 }, { -1e4, 1e4 }, { -1e30, 1e30 } } },
	{ "tan", math.tan, { { -4.0, 4.0 }, { -1e4, 1e4 }, { -1e30, 1e30 } } },
	{ "asin", math.asin, { { -1.25, 1.25 }, { -1e-3, 1e-3 } } },
	{ "acos", math.acos, { { -1.25, 1.25 }, { -1e-3, 1e-3 } } },
	{ "atan", math.atan, { { -4.0, 4.0 }, { -1e6, 1e6 } } },
	{
		"atan2",
		math.atan,
		{ { -4.0, 4.0, -4.0, 4.0 }, { -1e6, 1e6, -1.0, 1.0 } },
	},
	{ "exp", math.exp, { { -1.0, 1.0 }, { -110.0, 110.0 } } },
	{ "log", math.log, { { 0.0, 4.0 }, { 0.0, 1e30 } } },
	{
		"log2",
		function(x)
			return math.log(x, 2)
		end,
		{ { 0.0, 4.0 }, { 0.0, 1e30 } },
	},
	{
		"log10",
		function(x)
			return math.log(x, 10)
		end,
		{ { 0.0, 4.0 }, { 0.0, 1e30 } },
	},
	{
		"logb",
		function(x)
			return math.log(x, 3)
		end,
		{ { 0.0, 4.0 }, { 0.0, 1e30 } },
	},
	{
		"pow",
		function(x, y)
			return x ^ y
		end,
		{ { 0.0, 4.0, -30.0, 30.0 }, { -4.0, 4.0, -4.0, 4.0 } },
	},
	-- 指数 2 は乗算。結果が subnormal のところで powf(x, 2) と違う
	{
		"pow2",
		function(x)
			return x ^ 2
		end,
		{ { -1e-19, 1e-19 } },
	},
}

-- 線形合同法 (32bit で wrap する) の上位 24 bit から [lo, hi) の値を作る
local state
local function next_input(lo, hi)
	state = state * 1664525 + 1013904223
	return lo + (hi - lo) * (((state >> 8) & 0xffffff) / 16777216)
end

-- NaN の bit 列は CPU で違うので 1 つにまとめる
local function bits(x)
	if x ~= x then
		return 0x7fc00000
	end
	return (string.unpack("<i4", string.pack("<f", x)))
end

local function digest(fn, ranges)
	state = 1
	local h = -2128831035
	for _, r in ipairs(ranges) do
		for _ = 1, POINTS do
			local result
			if r[3] then
				local a = next_input(r[1], r[2])
				result = fn(a, next_input(r[3], r[4]))
			else
				result = fn(next_input(r[1], r[2]))
			end
			h = (h ~ bits(result)) * 16777619
		end
	end
	return h
end

local function verify()
	local failed = {}
	for _, case in ipairs(cases) do
		local name, got = case[1], digest(case[2], case[3])
		if got ~= expected[name] then
			failed[#failed + 1] = name .. " = " .. got
		end
	end
	return failed
end

local M = {}

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
end

function M.on_frame()
	local failed = verify()
	if #failed > 0 then
		print("FAIL math digests differ: " .. table.concat(failed, ", "))
		os.exit(1, true)
	end
	print("OK test_math_determinism")
	lub.app.quit()
end

return M

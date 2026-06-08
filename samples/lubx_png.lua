-- samples/lubx_png.lua
-- PNG helper kept out of lub.Io: load/write byte-backed RGBA8 images.

local M = {}
M.PENDING = "pending"
M.READY = "ready"
M.ERROR = "error"

local cache = {}

local function read_bytes(path)
	local f = io.open(path, "rb")
	if not f then
		return nil
	end
	local s = f:read("*a")
	f:close()
	return s
end

local function request_status(path)
	if type(request_file) == "function" then
		local st, err = request_file(path)
		if st == M.READY or st == M.PENDING or st == M.ERROR then
			return st, err
		end
		return M.ERROR, err or ("bad request_file status: " .. tostring(st))
	end
	if file_mtime(path) then
		return M.READY
	end
	return M.ERROR, "missing"
end

local function refresh(path)
	local mtime = file_mtime(path)
	if not mtime then
		local st, err = request_status(path)
		return nil, 0, st, err
	end
	local c = cache[path]
	if c and c.mtime == mtime then
		return c.parsed, c.hash, M.READY
	end
	local bytes = read_bytes(path)
	if not bytes then
		local st, err = request_status(path)
		if c then
			return c.parsed, c.hash, st, err
		end
		return nil, 0, st, err
	end
	local hash = fnv1a64(bytes)
	if c and c.hash == hash then
		c.mtime = mtime
		return c.parsed, hash, M.READY
	end

	local data, w, h, fmt, stride = png_load(path)
	if data == nil then
		if c then
			return c.parsed, c.hash, M.ERROR, "png_load failed"
		end
		return nil, 0, M.ERROR, "png_load failed"
	end

	local parsed = { bytes = data, w = w, h = h, fmt = fmt, stride = stride }
	cache[path] = { mtime = mtime, hash = hash, parsed = parsed }
	return parsed, hash, M.READY
end

function M.load(path)
	local parsed, ver, st, err = refresh(path)
	if not parsed then
		return nil, nil, nil, nil, nil, ver or 0, st or M.ERROR, err
	end
	return parsed.bytes, parsed.w, parsed.h, parsed.fmt, parsed.stride, ver, st or M.READY, err
end

function M.write(path, bytes, width, height, stride)
	return png_write(path, bytes, width, height, stride)
end

return M

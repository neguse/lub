-- samples/lub_io.lua
-- File-input helper with mtime fast-path + content hash version.
--
-- Usage:
--   local lub_io = require("lub_io")
--   local src,  ver = lub_io.load_text("foo.slang")
--   local tab,  ver = lub_io.load_floats("foo.verts.lua")
--   local px, w, h, fmt, ver = lub_io.load_png("foo.png")
--
-- Cache: path -> { mtime, bytes, hash, parsed }
-- Fast path: same mtime -> return cached parsed + hash (no read, no hash).
-- Slow path: stat, read bytes, fnv1a64 -> if hash differs, reparse.

local M = {}
local cache = {}

local function read_bytes(path)
   local f = io.open(path, "rb")
   if not f then return nil end
   local s = f:read("*a")
   f:close()
   return s
end

-- Returns (parsed, version). Returns nil on failure.
local function refresh(path, parse_fn)
   local mtime = file_mtime(path)
   if not mtime then return nil end
   local c = cache[path]
   if c and c.mtime == mtime then
      return c.parsed, c.hash
   end
   local bytes = read_bytes(path)
   if not bytes then return nil end
   local hash = fnv1a64(bytes)
   if c and c.hash == hash then
      c.mtime = mtime    -- 内容変わってない、mtime だけ更新
      return c.parsed, hash
   end
   local parsed = parse_fn(bytes, path)
   if parsed == nil then
      -- parse 失敗、cache 更新せずに前回値を維持
      if c then return c.parsed, c.hash end
      return nil
   end
   cache[path] = { mtime = mtime, bytes = bytes, hash = hash, parsed = parsed }
   return parsed, hash
end

function M.load_text(path)
   return refresh(path, function(s) return s end)
end

function M.load_floats(path)
   return refresh(path, function(src, p)
      local chunk, err = load(src, "@" .. p, "t")      -- 既定 env を使う
      if not chunk then
         print("lub_io.load_floats: parse error in " .. p .. ": " .. tostring(err))
         return nil
      end
      local ok, t = pcall(chunk)
      if not ok then
         print("lub_io.load_floats: exec error in " .. p .. ": " .. tostring(t))
         return nil
      end
      if type(t) ~= "table" then
         print("lub_io.load_floats: " .. p .. " did not return a table")
         return nil
      end
      return t
   end)
end

function M.load_png(path)
   -- PNG は parsed = { px = {...}, w, h, fmt }
   local parsed, ver = refresh(path, function(_bytes, p)
      -- _bytes は使わず C 側に再読み込みさせる (load_png は path を取る)
      local px, w, h, fmt = load_png(p)
      if px == nil then return nil end
      return { px = px, w = w, h = h, fmt = fmt }
   end)
   if not parsed then return nil end
   return parsed.px, parsed.w, parsed.h, parsed.fmt, ver
end

function M.load_gltf(path)
   local parsed, ver = refresh(path, function(_bytes, p)
      -- _bytes は使わず C 側に再読み込みさせる (load_gltf は path を取る)
      return load_gltf(p)
   end)
   if not parsed then return nil end
   return parsed, ver
end

-- mesh.positions (vec3 * N) + mesh.normals (vec3 * N) を
-- pos.x, pos.y, pos.z, n.x, n.y, n.z, ... 形式の平らな float table に詰める。
-- normals が nil の場合は (0, 0, 1) で埋める。
function M.interleave_pn(mesh)
   local n = mesh.vert_count
   local out = {}
   local pos = mesh.positions
   local nrm = mesh.normals
   for i = 0, n - 1 do
      local pi = i * 3
      out[#out + 1] = pos[pi + 1]
      out[#out + 1] = pos[pi + 2]
      out[#out + 1] = pos[pi + 3]
      if nrm then
         out[#out + 1] = nrm[pi + 1]
         out[#out + 1] = nrm[pi + 2]
         out[#out + 1] = nrm[pi + 3]
      else
         out[#out + 1] = 0
         out[#out + 1] = 0
         out[#out + 1] = 1
      end
   end
   return out
end

return M

-- samples/lub_io.lua
-- File-input helper with mtime fast-path + content hash version.
--
-- Usage:
--   local lub_io = require("lub_io")
--   local src,  ver, status = lub_io.load_text("foo.slang")
--   local tab,  ver, status = lub_io.load_floats("foo.verts.lua")
--
-- Cache: path -> { mtime, bytes, hash, parsed }
-- Fast path: same mtime -> return cached parsed + hash (no read, no hash).
-- Slow path: stat, read bytes, fnv1a64 -> if hash differs, reparse.

local M = {}
M.PENDING = "pending"
M.READY = "ready"
M.ERROR = "error"

local cache = {}

local function read_bytes(path)
   local f = io.open(path, "rb")
   if not f then return nil end
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
   if file_mtime(path) then return M.READY end
   return M.ERROR, "missing"
end

local function dirname(path)
   return path:match("^(.*[/\\])") or ""
end

local function requestable_uri(uri)
   local lower = uri:lower()
   if lower:match("^data:") or lower:match("^https?://") then return false end
   if uri:sub(1, 1) == "/" then return false end
   return true
end

local function blocking_gltf_uri(uri)
   local lower = uri:lower()
   return lower:match("%.bin$") or lower:match("%.glb$")
end

local function request_gltf_dependencies(src, path)
   local base = dirname(path)
   local pending_path = nil
   for uri in src:gmatch('"uri"%s*:%s*"([^"]+)"') do
      if requestable_uri(uri) and blocking_gltf_uri(uri) then
         local dep = base .. uri
         local st, err = request_status(dep)
         if st ~= M.READY then
            if st == M.ERROR then
               return M.ERROR, err or dep
            end
            pending_path = pending_path or dep
         end
      end
   end
   if pending_path then return M.PENDING, pending_path end
   return M.READY
end

-- Returns (parsed, version, status, error). Returns nil while pending/error.
local function refresh(path, parse_fn)
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
      if c then return c.parsed, c.hash, st, err end
      return nil, 0, st, err
   end
   local hash = fnv1a64(bytes)
   if c and c.hash == hash then
      c.mtime = mtime    -- 内容変わってない、mtime だけ更新
      return c.parsed, hash, M.READY
   end
   local parsed, parse_status, parse_err = parse_fn(bytes, path)
   if parsed == nil then
      -- parse 失敗、cache 更新せずに前回値を維持
      local st = parse_status or M.ERROR
      if c then return c.parsed, c.hash, st, parse_err end
      return nil, 0, st, parse_err
   end
   cache[path] = { mtime = mtime, bytes = bytes, hash = hash, parsed = parsed }
   return parsed, hash, M.READY
end

function M.load_text(path)
   return refresh(path, function(s) return s end)
end

function M.load_floats(path)
   return refresh(path, function(src, p)
      local chunk, err = load(src, "@" .. p, "t")      -- 既定 env を使う
      if not chunk then
         print("lub_io.load_floats: parse error in " .. p .. ": " .. tostring(err))
         return nil, M.ERROR, err
      end
      local ok, t = pcall(chunk)
      if not ok then
         print("lub_io.load_floats: exec error in " .. p .. ": " .. tostring(t))
         return nil, M.ERROR, t
      end
      if type(t) ~= "table" then
         print("lub_io.load_floats: " .. p .. " did not return a table")
         return nil, M.ERROR, "not a table"
      end
      return t
   end)
end

function M.load_gltf(path)
   local parsed, ver, st, err = refresh(path, function(bytes, p)
      local dep_status, dep_err = request_gltf_dependencies(bytes, p)
      if dep_status ~= M.READY then
         return nil, dep_status, dep_err
      end
      -- _bytes は使わず C 側に再読み込みさせる (load_gltf は path を取る)
      local mesh = load_gltf(p)
      if mesh == nil then return nil, M.ERROR, "load_gltf failed" end
      return mesh
   end)
   if not parsed then return nil, ver or 0, st or M.ERROR, err end
   return parsed, ver, st or M.READY, err
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

-- mesh.positions + normals + uvs を pos.xyz, n.xyz, uv.xy (stride 8) に詰める。
-- normals 欠損は (0,0,1)、uvs 欠損は (0,0) で埋める。material shader 用。
function M.interleave_pnu(mesh)
   local n = mesh.vert_count
   local out = {}
   local pos = mesh.positions
   local nrm = mesh.normals
   local uv = mesh.uvs
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
         out[#out + 1] = 0; out[#out + 1] = 0; out[#out + 1] = 1
      end
      if uv then
         local ui = i * 2
         out[#out + 1] = uv[ui + 1]
         out[#out + 1] = uv[ui + 2]
      else
         out[#out + 1] = 0; out[#out + 1] = 0
      end
   end
   return out
end

-- mesh.positions + normals + uvs + tangents を
-- pos.xyz, n.xyz, uv.xy, tangent.xyzw (stride 12) に詰める。
-- tangent 欠損時は w=0 にして shader 側が derivative TBN にfallbackできるようにする。
function M.interleave_pnut(mesh)
   local n = mesh.vert_count
   local out = {}
   local pos = mesh.positions
   local nrm = mesh.normals
   local uv = mesh.uvs
   local tan = mesh.tangents
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
         out[#out + 1] = 0; out[#out + 1] = 0; out[#out + 1] = 1
      end
      if uv then
         local ui = i * 2
         out[#out + 1] = uv[ui + 1]
         out[#out + 1] = uv[ui + 2]
      else
         out[#out + 1] = 0; out[#out + 1] = 0
      end
      if tan then
         local ti = i * 4
         out[#out + 1] = tan[ti + 1]
         out[#out + 1] = tan[ti + 2]
         out[#out + 1] = tan[ti + 3]
         out[#out + 1] = tan[ti + 4]
      else
         out[#out + 1] = 1; out[#out + 1] = 0; out[#out + 1] = 0; out[#out + 1] = 0
      end
   end
   return out
end

return M

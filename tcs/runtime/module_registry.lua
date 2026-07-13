-- TinyC# ModuleRegistry (doc/incremental-module-compilation-design.md §11)
-- 全 module の stable type table を所有し、descriptor batch を
-- declare (pre-zero) → define → initialize の三段階で apply する。
-- type table の identity は reload をまたいで維持され、既存 instance の
-- metatable lookup が次の呼び出しから新 method を見る。
local Registry = {}
Registry.__index = Registry

local M = {}

function M.new(host)
  local self = setmetatable({
    host = host,
    types = {}, -- typeId -> stable type table
    alias = {}, -- Lua global 名 -> typeId
    modules = {}, -- moduleId -> { hash, types(meta 配列) }
    revision = 0,
    wrapper = {}, -- thin entry wrapper (identity 安定)
    entry_type = nil,
  }, Registry)
  -- module 専用 read-only _ENV。emitted type alias → host global の順で
  -- 解決し、未宣言 global への write は error (§11)。
  self.env = setmetatable({}, {
    __index = function(_, k)
      local id = self.alias[k]
      if id then
        return self.types[id]
      end
      return host[k]
    end,
    __newindex = function(_, k)
      error("tcs registry: write to undeclared global: " .. tostring(k), 2)
    end,
  })
  return self
end

local function keyset(list)
  local s = {}
  for _, k in ipairs(list or {}) do
    s[k] = true
  end
  return s
end

local function keyset_statics(meta)
  local s = {}
  for _, f in ipairs(meta.statics or {}) do
    s[f.key] = true
  end
  return s
end

local function find_type_meta(mod_record, type_id)
  if not mod_record then
    return nil
  end
  for _, t in ipairs(mod_record.types) do
    if t.id == type_id then
      return t
    end
  end
  return nil
end

-- batch = { revision, entry = { type, keys }?, modules = { descriptor... } }
-- descriptor = { id, hash, types = { {id,name,kind,base?,statics,keys} },
--                define = function(_ENV), inits = { [typeId]=fn },
--                initfns = { [typeId] = { [key]=fn } } }
-- modules は常に全 active module (full snapshot)。hash が前回 apply と同じ
-- module は skip する。
function Registry:applyBatch(batch)
  if batch.revision <= self.revision then
    return {
      ok = true,
      skipped = true,
      revision = self.revision,
      wrapper = self.wrapper,
    }
  end

  -- 対象 module の選別 (hash unchanged は skip)
  local changed = {}
  local present = {}
  for _, m in ipairs(batch.modules) do
    present[m.id] = true
    local prev = self.modules[m.id]
    if not (prev and prev.hash == m.hash) then
      changed[#changed + 1] = m
    end
  end
  -- full snapshot に無い既知 module = 削除。live registry からの type 削除は
  -- conservative に restart 対象で、host が分類する (§10)。ここに来たら契約違反。
  for id in pairs(self.modules) do
    if not present[id] then
      error("tcs registry: module removed without restart: " .. id)
    end
  end

  -- phase 1: declare + pre-zero (§11.1)。全 type table を先に確保するので
  -- module 順に依存せず cross-module 継承を link できる。
  local new_types = {} -- typeId -> true (今回初出。initialize で full init)
  for _, m in ipairs(changed) do
    local prev = self.modules[m.id]
    for _, t in ipairs(m.types) do
      local existing = self.alias[t.name]
      if existing and existing ~= t.id then
        error("tcs registry: type alias collision: " .. t.name)
      end
      self.alias[t.name] = t.id
      local tbl = self.types[t.id]
      if not tbl then
        tbl = {}
        self.types[t.id] = tbl
        new_types[t.id] = true
      end
      local prev_meta = find_type_meta(prev, t.id)
      local prev_statics = prev_meta and keyset_statics(prev_meta) or {}
      for _, s in ipairs(t.statics or {}) do
        -- fresh type は全 static、既存 type は新規 static だけ pre-zero
        if s.default ~= nil and (new_types[t.id] or not prev_statics[s.key]) then
          rawset(tbl, s.key, s.default)
        end
      end
    end
  end

  -- phase 2: define。stable table 上で method/accessor/metamethod/enum を
  -- 差し替え、旧 artifact にだけあった owned key を削除する (§11.2)。
  for _, m in ipairs(changed) do
    local prev = self.modules[m.id]
    m.define(self.env)
    for _, t in ipairs(m.types) do
      local prev_meta = find_type_meta(prev, t.id)
      if prev_meta then
        local tbl = self.types[t.id]
        local now = keyset(t.keys)
        for _, k in ipairs(prev_meta.keys) do
          if not now[k] then
            rawset(tbl, k, nil)
          end
        end
        -- base link が消えた場合は metatable を外す (変更は define が
        -- setmetatable し直す)
        if prev_meta.base and not t.base then
          setmetatable(tbl, nil)
        end
      end
    end
  end

  -- phase 3: initialize。fresh type は type 単位 thunk を全部、既存 type は
  -- 新規かつ副作用なし (pure) の static field だけ (§11.1/§11.3)。
  for _, m in ipairs(changed) do
    local prev = self.modules[m.id]
    for _, t in ipairs(m.types) do
      if new_types[t.id] then
        local init = m.inits and m.inits[t.id]
        if init then
          init(self.env)
        end
      else
        local prev_meta = find_type_meta(prev, t.id)
        local prev_statics = prev_meta and keyset_statics(prev_meta) or {}
        for _, s in ipairs(t.statics or {}) do
          if not prev_statics[s.key] then
            local fns = m.initfns and m.initfns[t.id]
            local fn = fns and fns[s.key]
            if fn then
              fn(self.env)
            elseif not s.pure then
              error("tcs registry: impure new static needs restart: "
                .. t.id .. "." .. s.key)
            end
          end
        end
      end
    end
  end

  -- entry wrapper 更新。wrapper と delegate の identity は維持し、
  -- key の増減だけ反映する (§14.2)。
  if batch.entry then
    self.entry_type = batch.entry.type
    local want = keyset(batch.entry.keys)
    for k in pairs(self.wrapper) do
      if not want[k] then
        self.wrapper[k] = nil
      end
    end
    for _, k in ipairs(batch.entry.keys) do
      if rawget(self.wrapper, k) == nil then
        self.wrapper[k] = function(...)
          return self.types[self.entry_type][k](...)
        end
      end
    end
  end

  -- commit: module 記録と revision を更新
  for _, m in ipairs(changed) do
    self.modules[m.id] = { hash = m.hash, types = m.types }
  end
  self.revision = batch.revision

  return {
    ok = true,
    revision = batch.revision,
    applied = #changed,
    wrapper = self.wrapper,
  }
end

return M

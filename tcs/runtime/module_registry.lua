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
  -- hot reload 用の weak instance registry (il-design §6) は host 側で共有
  -- する。define chunk 先頭の guard 代入 (`__tcs_instances = ... or ...`) を
  -- 通すため、このキーのみ host への write を許す
  host.__tcs_instances = host.__tcs_instances
    or setmetatable({}, { __mode = "k" })
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
    __newindex = function(_, k, v)
      if k == "__tcs_instances" then
        host[k] = v
        return
      end
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

-- transaction log (§11.4)。保存/復元は rawget/rawset + presence sentinel。
-- 通常 lookup で保存すると inherited key を own key として復元してしまい、
-- 以後の base patch が derived に届かなくなる。
local function tx_new()
  return { keys = {}, metas = {}, created_types = {}, alias = {} }
end

local function tx_save_key(tx, tbl, k)
  local rec = tx.keys[tbl]
  if not rec then
    rec = {}
    tx.keys[tbl] = rec
  end
  if rec[k] == nil then
    local v = rawget(tbl, k)
    rec[k] = { present = v ~= nil, value = v }
  end
end

local function tx_save_meta(tx, tbl)
  if tx.metas[tbl] == nil then
    tx.metas[tbl] = { mt = getmetatable(tbl) }
  end
end

local function tx_rollback(self, tx)
  for tbl, keys in pairs(tx.keys) do
    for k, rec in pairs(keys) do
      if rec.present then
        rawset(tbl, k, rec.value)
      else
        rawset(tbl, k, nil) -- 元々無かった own key は削除で戻す
      end
    end
  end
  for tbl, rec in pairs(tx.metas) do
    setmetatable(tbl, rec.mt)
  end
  for id in pairs(tx.created_types) do
    self.types[id] = nil
  end
  for name, rec in pairs(tx.alias) do
    self.alias[name] = rec.value -- 新規なら nil に戻る
  end
end

-- commit ACK (§13.1)。channel は host の print relay 上の構造化行。
-- batch.ack が真のときだけ出す (bridge snapshot 用)。
local function emit_ack(self, batch, ok, ms, err)
  if not batch.ack then
    return
  end
  local p = self.host.print
  if not p then
    return
  end
  local e = ""
  if err then
    e = string.format(',"error":"%s"',
      tostring(err):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\n", "\\n"))
  end
  p(string.format('@@tcs_commit {"revision":%d,"ok":%s,"commitTimeMs":%s%s}',
    batch.revision, ok and "true" or "false",
    string.format("%.2f", ms or 0), e))
end

local function now_ms(self)
  local os_ = self.host.os
  if os_ and os_.clock then
    return os_.clock() * 1000
  end
  return 0
end

-- batch = { revision, ack?, entry = { type, keys }?, modules = { descriptor... } }
-- descriptor = { id, hash, types = { {id,name,kind,base?,statics,keys} },
--                define = function(_ENV), inits = { [typeId]=fn },
--                initfns = { [typeId] = { [key]=fn } } }
-- modules は常に全 active module (full snapshot)。hash が前回 apply と同じ
-- module は skip し、変更分だけ atomic に apply する。失敗時は transaction
-- log で完全に戻してから error を再送出する (lume.hotswap が old module を
-- 維持できるように)。
function Registry:applyBatch(batch)
  if batch.revision <= self.revision then
    -- 同一 revision の再適用 (host の ACK retry による entry 再書き込み) には
    -- ACK を返し直す。古い revision は黙って捨てる。
    if batch.revision == self.revision then
      emit_ack(self, batch, true, 0)
    end
    return {
      ok = true,
      skipped = true,
      revision = self.revision,
      wrapper = self.wrapper,
    }
  end
  local t0 = now_ms(self)
  local was_fresh = self.revision == 0

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
  -- mutation 前の検証 (§11.4 step 1)。full snapshot に無い既知 module = 削除。
  -- live registry からの削除は conservative に restart 対象で host が分類する。
  for id in pairs(self.modules) do
    if not present[id] then
      local err = "tcs registry: module removed without restart: " .. id
      emit_ack(self, batch, false, now_ms(self) - t0, err)
      error(err, 0)
    end
  end
  for _, m in ipairs(changed) do
    for _, t in ipairs(m.types) do
      local existing = self.alias[t.name]
      if existing and existing ~= t.id then
        local err = "tcs registry: type alias collision: " .. t.name
        emit_ack(self, batch, false, now_ms(self) - t0, err)
        error(err, 0)
      end
    end
  end

  -- 影響し得る state を mutation 前にすべて transaction log へ保存する。
  -- 対象は changed module の own type の key (新旧 definition/static key の
  -- 和集合) と metatable、alias。define は module env 経由でしか名前解決
  -- できないため、これ以外へ書けない (__newindex error)。
  local tx = tx_new()
  for _, m in ipairs(changed) do
    local prev = self.modules[m.id]
    for _, t in ipairs(m.types) do
      tx.alias[t.name] = tx.alias[t.name]
        or { value = self.alias[t.name] }
      local tbl = self.types[t.id]
      if tbl then
        tx_save_meta(tx, tbl)
        local prev_meta = find_type_meta(prev, t.id)
        if prev_meta then
          for _, k in ipairs(prev_meta.keys) do
            tx_save_key(tx, tbl, k)
          end
          for _, f in ipairs(prev_meta.statics or {}) do
            tx_save_key(tx, tbl, f.key)
          end
        end
        for _, k in ipairs(t.keys) do
          tx_save_key(tx, tbl, k)
        end
        for _, f in ipairs(t.statics or {}) do
          tx_save_key(tx, tbl, f.key)
        end
      end
    end
  end

  local new_types = {} -- typeId -> true (今回初出。initialize で full init)
  local ok, err = pcall(function()
    -- phase 1: declare + pre-zero (§11.1)。全 type table を先に確保するので
    -- module 順に依存せず cross-module 継承を link できる。
    for _, m in ipairs(changed) do
      local prev = self.modules[m.id]
      for _, t in ipairs(m.types) do
        self.alias[t.name] = t.id
        local tbl = self.types[t.id]
        if not tbl then
          tbl = {}
          self.types[t.id] = tbl
          new_types[t.id] = true
          tx.created_types[t.id] = true
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
  end)

  if not ok then
    tx_rollback(self, tx)
    emit_ack(self, batch, false, now_ms(self) - t0, err)
    error(err, 0)
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

  -- commit: module 記録と revision を更新。commit 後は失敗し得る処理を
  -- 置かない (§14.2)。ACK を出してから戻る。
  for _, m in ipairs(changed) do
    self.modules[m.id] = { hash = m.hash, types = m.types }
  end
  self.revision = batch.revision
  emit_ack(self, batch, true, now_ms(self) - t0)

  -- hot apply 後は entry の onReload を呼ぶ。module mode は method-body edit で
  -- static を保持するため、旧 hotswap の「chunk 再実行で dirty フラグが初期値に
  -- 戻る」イディオムが効かない。コード由来の derived data (SDF mesh 等) の
  -- 再構築はこの hook を境界にする。commit は確定済みなので失敗は警告のみ。
  if not was_fresh and #changed > 0 and batch.entry then
    local et = self.types[batch.entry.type]
    local f = et and rawget(et, "onReload")
    if type(f) == "function" then
      local ok2, err2 = pcall(f)
      if not ok2 and self.host.print then
        self.host.print("tcs registry: onReload error: " .. tostring(err2))
      end
    end
  end

  return {
    ok = true,
    revision = batch.revision,
    applied = #changed,
    wrapper = self.wrapper,
  }
end

return M

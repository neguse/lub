-- Sys.Require("require_helper") で読む Lua の module。
-- RequireHelper.ref.cs の型 (requirehelper.api) に合わせて global を置く。
requirehelper = {
  api = {
    twice = function(x)
      return x * 2
    end,
  },
}
return requirehelper

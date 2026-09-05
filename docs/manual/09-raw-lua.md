# raw Lua で書く

lub のゲームは Lua だけでも書ける。runtime API は C runtime が作る `lub`
table(`lub.gfx` / `lub.input` / `lub.io` / `lub.phys2d` / ...)で、名前は
C# の面(`Gfx.BeginPass`)を snake_case に写したもの(`lub.gfx.begin_pass`)。
enum は namespace 直下の大文字の定数(`lub.gfx.VERTEX`)か、"begin" のような
文字列(API reference の Lua 欄を見る)。

## entry

entry は `on_init` / `on_frame` / `on_event` / `on_quit` を持つ table を返す
module。直パスで起動し、編集すると hot reload される。

```lua
local M = {}

function M.on_init()
	lub.config({ width = 640, height = 480 })
end

function M.on_frame(dt)
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.1, 0.1, 0.2, 1.0 } })
	lub.gfx.end_pass()
end

return M
```

```
lub samples/27_lua_triangle/27_lua_triangle.lua
```

## 値の受け渡し

- option は table(`{ shader = s, depth = false }`)。省略した field は既定値。
- 多値を返す関数は Lua の多値(`local text, version, status, err = lub.io.load_text(path)`)。
- runtime 所有の byte 列(`lub.png.load` / readback / glyph の bitmap)は
  frame の終わりまで有効な view。`#v` と `v[i]`(1 始まり)、`v:get(i)`
  (0 始まり)で読め、`tostring(v)` で Lua の文字列に写せる。frame を跨いで
  持つと error になる。
- 見つからない問い合わせは `nil, "not found"`。誤用は Lua の error。

## lubx を使う

SpriteBatch / Text / Camera など実装ライブラリ(lubx)の正は C#
(`cs-lib/`)で、tcs が生成した Lua を `samples/lubx.lua` に checkin してある。
`require` が型の table を返す。C# の名前は snake_case に写る
(`SpriteBatch.new` / `batch:rect(...)` / `Color.rgb(...)`)。

```lua
local lubx = require("lubx")
local batch = lubx.SpriteBatch.new(640, 480, "shader", "batch", true)

function M.on_frame(dt)
	lub.gfx.begin_pass({ target = lub.gfx.main_tex })
	batch:begin()
	batch:rect(10, 10, 32, 32, lubx.Color.rgb(1, 0.5, 0, 1))
	batch:flush()
	lub.gfx.end_pass()
end
```

lubx を直したいときは C# を直して `scripts/gen-lubx-lua.sh` で再生成する
(生成物を直接編集しない)。例は `samples/28_lua_sprites/`。

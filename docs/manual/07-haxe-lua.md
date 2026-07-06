# Haxe → Lua の注意点

ゲームコードは Haxe で書くが、実行されるのは transpile された Lua で、
runtime API の実体は Lua 関数。境界でいくつか落とし穴がある。

## 配列リテラルはそのまま渡さない

Haxe の `Array` は Lua 上では 0-indexed のオブジェクトになり、lub の C 側が
期待する 1-indexed の Lua table と噛み合わない。runtime API に配列を渡す
ときは `lua.Table.fromArray` で明示変換する:

```haxe
// NG: clear_color: [0.1, 0.1, 0.2, 1.0]
Gfx.beginPass({
	target: Gfx.mainTex,
	clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0]),
});
```

named field の anonymous structure(`{target: x}` など)はそのまま渡してよい。
対象は「配列」だけ。

## 環境変数は lua.Os.getenv

Haxe stdlib の `Sys.getEnv()` は使えない(lua target の `Sys` が `luv` を
require するため)。代わりに `lua.Os.getenv()` を使う。`lubx.Boot.config`
が読む `LUB_BACKEND` もこの経路。

なお `lub.Sys`(mtime / FPS などの runtime primitive)と Haxe stdlib の
`Sys` は名前が衝突するので、両方に触れるファイルでは import に注意。

## multi-return の戻り値

`Io.loadText` などの戻り値は Lua の多値戻り(`@:multiReturn`)。Haxe からは
普通のオブジェクトと同じようにフィールドアクセスすればよく、transpiler が
展開してくれる。Lua 側から同じ API を直接呼ぶ場合は
`local text, version, status = load_text(path)` のように多値で受ける。

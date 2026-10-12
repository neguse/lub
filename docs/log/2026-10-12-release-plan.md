# リリースで build 済みのものを配る方針

> 記録: 2026-10-12 時点で決めた方針と、まだ決めていないこと。実装の段取りはまだ無い。配布の形は 2026-07-23 のパッケージング設計 (`2026-07-23-packaging-design.md`) から変わっている。

## 背景

- GitHub の Release はまだ無い。外のリポでゲームを作るには、lub を clone して submodule を取り、platform ごとに build し、`templates/game` の `LubRoot` と `LubNativeDir` を書き換える必要がある。
- CI は tcs→Lua の player を中心に全 platform を毎 PR で回していて、iOS の simulator の job (約 9.5 分) が律速になっている。

## 決めたこと

- リリースでは build 済みのものを配り、使う人が lub とその依存 (SDL、Box2D 等) を build しなくて済むようにする。
- ゲームを出す主力の経路は CoreCLR (.NET 実行) と tcs2c。tcs→Lua の player は開発用の道具 (hot reload)。2026-07-23 の設計の「player と生成 Lua の snapshot を配る」形は主力ではなくなった。
- 配る経路は NuGet を本線にする。主力の経路はどちらも .NET を前提にするので、使う側に足すものが無い。
  - CoreCLR: facade (`Lub.dll`) と、OS ごとの build 済み `liblub` (`runtimes/<rid>/native`)。stub、analyzer、TinySystem も同じ package で届け、csproj の `PackageReference` 1 行で使えるようにする。
  - tcs2c: tcs2c と lub-gen、OS ごとの liblub の static library と header。使う側に C コンパイラ (web は emsdk も) は残る。
- iOS は tcs2c で出す。NativeAOT は後で試す。
- CI は、PR では速く回すことを優先する。配る package を全 platform で検査する (package から CoreCLR と tcs2c のゲームが build できて動く) のはリリースの作業でやる。iOS の検査は、Lua の player を simulator で動かすことから、tcs2c のゲームを動かすことに移す。

## 置き場

- NuGet は公開の registry でなくても、取得元 (source) に folder や静的な file で作った v3 の feed を指定できる。nuget.org (account と API key、公開した版は消せない) や GitHub Packages (取る側に token が要る) には置かず、lub.neguse.net (Cloudflare Workers の静的 asset) に静的な feed を置く。
- Workers の静的 asset は 1 file 25 MiB が上限。native の library は依存ごとに package を分け、lub 本体の package がそれに依存する形にする (依存の package の native library も使う側の出力に copy される)。どの package も上限に収まり、毎回変わるのは lub 本体と tcs / tcs2c だけになる。slang などは版が変わったときだけ上げる。
- D3D12 Agility は Microsoft の `Microsoft.Direct3D.D3D12` (nuget.org、更新が続いている) に依存してよい。dxcompiler の `Microsoft.Direct3D.DXC` は最新が 2022 年の版 (101.7.2207.25) で lub の使う版より古いので、自前の package にする。

2026-10-12 に測った大きさ (zip の最大圧縮。nupkg と同じ形式):

| package にする中身 | 圧縮後 |
| --- | --- |
| lub 本体 (liblub、SDL3、OpenXR の loader) | 4.2 MiB (Linux x64 で実測。他の OS も同程度と仮定) |
| slang (slang-compiler、Windows は slang.dll も) | Windows 12.3 / macOS 10.8 / Linux 11.6 MiB (実測) |
| dxcompiler (Windows) | 7.6 MiB (実測) |
| D3D12Core (Windows) | 1.7 MiB (実測) |
| tcs (Transpiler、Roslyn 込み) / tcs2c | 各 5 MiB (実測) |
| facade (`Lub.dll`) | 0.2 MB (実測) |

- slang の glslang 系 (`slang-glslang`、`slang-glsl-module`) は、Linux で外しても sdlgpu / vulkan の描画と compute が通った (Windows と macOS は未確認)。上の大きさはこれを外した値。
- Windows の build は slang の `bin` を丸ごと exe の隣に copy していて、`slang-llvm.dll` (110 MB) なども入る。配るものは要る DLL に絞る。

## Claude から動かす経路

claude.ai の Claude が C# のゲームを書き、会話の中の artifact で動かして見せられるようにする。

- artifact の page は、script を cdnjs / jsDelivr の `/npm/` / unpkg からしか読めず、外への fetch は全部止まる (自前の domain、jsDelivr の `/gh/`、raw.githubusercontent.com も読めない)。WebAssembly と blob の URL の Worker / module は使える。page は 16 MB まで。2026-10-12 に試して確かめた。
- `assets` を宣言した artifact は organization の中だけの公開になり、別の organization の artifact から asset を直接 copy できない。なので runtime は asset にせず、page 1 枚に埋め込む。
- 変換は Claude の sandbox でする。sandbox は github.com (release の file、tarball、raw も) と npm / PyPI に届き、node 22 がある。nuget.org と lub.neguse.net には既定では届かない。
- lub のリリースに、Claude 向けの道具一式を GitHub の release の file として載せる。中身は tcs と slang の wasm (browser 向けの build を node で動かす。2026-10-12 に動くことを確かめた)、lub の player の wasm、page の雛形、C# から HTML 1 枚を作る node の CLI、使い方の手引き。
- CLI は tcs で C# を Lua にし、`Gfx.UseShader` などに渡る定数の shader のソースを拾って slang で WGSL にし (web と同じ手直しを通す)、player と Lua と shader の表を page 1 枚に詰める。組み込み (cs-lib) の shader も表に入れる。定数でない shader は変換できず、実行時に error にする。
- artifact は player と Lua と shader の表だけを持つ (2〜3 MB の見込み)。tcs と slang を page に入れる形 (試作では tcs を入れて 8.2 MiB、slang も入れると約 19 MiB で上限を超える) より小さく、開くたびの変換も無い。
- 試作 (2026-10-12): runtime を埋め込んだ page 1 枚を、別の organization の account の Claude が複製して C# だけ書き換えると、そのゲームが artifact で動いた。

## まだ決めていないこと

- player (tcs→Lua) の配り方: OS ごとの zip か dotnet tool か。
- リリースの単位と頻度 (tag か nightly か) と版の付け方。
- PR の CI に何を残すか。
- web の player (wasm + js) をリリースに含めるか。
- feed を生成して deploy する手順 (web の deploy と同じ wrangler か、別の Worker か)。

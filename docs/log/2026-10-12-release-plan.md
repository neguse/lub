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

- NuGet は公開の registry でなくても、取得元 (source) に folder や静的な file で作った v3 の feed を指定できる。nuget.org (account と API key、公開した版は消せない) や GitHub Packages (取る側に token が要る) は使わず、Cloudflare に静的な feed を置く。
- `.nupkg` は R2 に置く。lub.neguse.net (Workers の静的 asset) は 1 file 25 MiB が上限で、Windows の native package がそれに届く (下の大きさ)。R2 は 1 object 約 5 TiB まで、転送料は無料、無料枠は保存 10 GB-月。feed の index は lub.neguse.net か、R2 に付けた custom domain に置く。

2026-10-12 に測った大きさ (zip の最大圧縮。nupkg と同じ形式):

| 中身 | 圧縮後 |
| --- | --- |
| Linux x64 の native (liblub、SDL3、OpenXR の loader、slang-compiler) | 15 MiB (実測) |
| macOS arm64 の native | 約 14 MiB (slang の部分 10 MiB は実測、liblub と SDL3 は Linux と同じと仮定) |
| Windows x64 の native (上に加えて dxcompiler、D3D12Core、slang.dll) | 約 25 MiB (DLL の部分 21 MiB は実測、lub.dll と SDL3 は仮定) |
| tcs (Transpiler、Roslyn 込み) / tcs2c | 各 5 MiB (実測) |
| facade (`Lub.dll`) | 0.2 MB (実測) |

- slang の glslang 系 (`slang-glslang`、`slang-glsl-module`) は、Linux で外しても sdlgpu / vulkan の描画と compute が通った (Windows と macOS は未確認)。上の大きさはこれを外した値。
- Windows の build は slang の `bin` を丸ごと exe の隣に copy していて、`slang-llvm.dll` (110 MB) なども入る。そのまま配ると Windows の package は約 70 MiB になるので、配るものは要る DLL に絞る。

## まだ決めていないこと

- player (tcs→Lua) の配り方: OS ごとの zip か dotnet tool か。
- リリースの単位と頻度 (tag か nightly か) と版の付け方。
- PR の CI に何を残すか。
- web の player (wasm + js) をリリースに含めるか。
- feed の index を置く場所 (lub.neguse.net か R2 の custom domain か) と、feed を生成して upload する手順。

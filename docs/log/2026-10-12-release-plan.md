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

## まだ決めていないこと

- player (tcs→Lua) の配り方: OS ごとの zip か dotnet tool か。
- リリースの単位と頻度 (tag か nightly か) と版の付け方。
- PR の CI に何を残すか。
- web の player (wasm + js) をリリースに含めるか。
- NuGet への公開の API key (人の作業)。

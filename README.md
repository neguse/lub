# lub

`lub` は、コードを中心としたゲーム開発のためのゲームエンジン。
lubの目的は、開発における試行の繰り返しの摩擦を極力無くし、クリエイターがとことんまでこだわれる環境を提供すること。

## lubの特徴

- tcs(C# subsetのTinyC#言語)+lider(専用IDE)による超高速なホットリロードとリリースビルドでの実行性能を両立
- 同じコードをもとにマルチプラットフォームに対応(Windows/Mac/Linux/iOS/Android/Web)
- モダンな3D表現を実現可能な最小限かつimmediateなAPIを提供

## リファレンス

- [lub.neguse.net](https://lub.neguse.net): 動作するWeb版
- [lub.neguse.net/manual](https://lub.neguse.net/manual): 利用者むけマニュアル
- [docs/develop.md](docs/develop.md): lub自体の開発方法

## ライセンス

- lub 本体(C ランタイム / web のサイト / lider / samples / `cs-lib`)は MIT(`LICENSE`)。
- バンドル/リンクする第三者依存(SDL3 / Slang / TinyC# の runtime 等)は `THIRD_PARTY_LICENSES.md` を参照。

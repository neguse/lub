# luoc — TinyC# IL→C release backend (first milestone)

`luoc` は TinyC# source を `IlExport.Export` へ渡し、release 用の C source を
生成する .NET 10 console application。class は `calloc` した素の C struct、
array は型付き要素を持つ連続 allocation へ lower する。

## 使い方

```sh
dotnet run --project luoc -- --entry SpriteUpdate \
  ../tcs/Transpiler.Tests/DigestKernels/sprite_update.cs -o sprite_update.c
gcc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  sprite_update.c -o sprite_update
./sprite_update
```

`--entry` は `static void Main()` が複数ある場合に必要。第一 milestone では
`Console.WriteLine(float)` (`IlCall("print")`) を stdout 出力ではなく、f32 の
bit 列を直接 FNV-1a digest へ投入する観測 sink として実装する。生成 executable
は最後に 8 桁の digest を 1 行出力する。

3 digest kernel の一括検証:

```sh
bash luoc/verify-digests.sh
```

## 対応 slice

- static method、`i32` / `f32` / `bool`、static/instance field
- `IlLocal` / `IlAssign` / `IlIf` / `IlWhile` / `IlRepeat` /
  `IlNumericFor` / `IlDo` / break / continue / return
- 数値・比較・論理・bit 演算、`IlField`、0-based `IlIndex`、array Length
- `IlCall`: 同一 program の static method、`__tcs_idiv`、`__tcs_irem`、`print`
- 引数なし `IlNewObj`: zero initialization 後に constant field initializer を適用
- 1 次元 `new T[n]`: `n` は i32 local または int constant

これ以外の node、継承、instance method、overload、引数つき constructor、
List/Dictionary/string は node 名または対象を含む明示 error で拒否する。

## IlExport v0 の暫定 bridge

実行文と式は `IlExport` の IL だけから生成する。ただし v0 metadata には method
の parameter/return type、`new T[n]` の element type/length、field initializer が
無い。この 3 情報だけは、同じ入力 source を Roslyn で再度 bind した
`SourceFacts` から補う。式や制御フローを source syntax から再生成しない。

この bridge は `IlExport` が typed signature / typed array creation / initializer
IL を公開した時点で削除対象。現在は constant field initializer と単純な array
length 以外を安全側で拒否する。

生成 C は strict f32 bit literal、null/bounds/division fault、C# shift count masking
を runtime helper で実装する。複数の faultable operand の左→右評価には GCC/Clang
statement expression を使うため、第一 milestone の C dialect は GNU C。

# tcs2c — TinyC# IL→C release backend

`tcs2c` は TinyC# source を `IlExport.Export` へ渡し、release 用の GNU C source
を生成する .NET 10 console application。class は type id 付き `calloc` struct、
array と `List<int>` / `List<float>` は型付き連続 buffer へ lower する。

## 使い方

```sh
dotnet run --project tcs2c -- ../tcs/samples/collision.cs -o collision.c
gcc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  collision.c -o collision
./collision
```

単一の `static void Main()` があれば生成 executable の entry にする。複数ある
場合は `--entry CLASS` で選ぶ。class library sample のように `Main` が無い入力も
全 method を C へ変換し、static initializer だけを実行する no-op entry を付ける。

## 対応 slice（第二 milestone）

- static / instance method（exact class dispatch。virtual / inheritance は未対応）
- `i32` / `f32` / `bool` / immutable byte-string / class reference
- type id による exact `IlIsType`（null は false）
- `IlNewArray` の固定長連続配列
- `IlTable`、`List<int>` / `List<float>` の growable 連続 buffer、Add / Count /
  0-based index / `IlForeachList`
- `IlTernary`、string concat、`tostring`
- `Console.WriteLine` (`IlCall("print")`): i32 10進、f32 shortest round-trip、
  bool `true` / `false`、string byte 列
- 第一 milestone の数値・制御 flow、bounds/null/division fault

通常の `print` は stdout へ値を出す。digest kernel 回帰用だけは
`--digest-f32` を付け、各 f32 の bit 列を FNV-1a へ直接投入する。

```sh
TCS_ROOT=../tcs bash tcs2c/verify-digests.sh
```

## IlExport 契約

型と初期化情報は T228 契約 (`IlMethodInfo.ReturnType` / `ParameterTypes`、
`IlFieldInfo.Init`、`IlNewArray.ElementType` / `Length`、`IlTable.ElementType`) だけ
から読む。第一 milestone の `SourceFacts` / Roslyn 再解析 bridge は廃止した。

現契約には constructor 本文と local の宣言型が無い。そのためこの slice は、
initializer 付き local の型を IL から推論し、引数付き `IlNewObj` は引数を宣言順の
instance field へ代入する positional constructor に限定する（0 引数 default
constructor も可）。initializer 無し local、positional で表せない constructor、
method overload、継承、List の int/float 以外は対象を含む明示 error で拒否する。

生成 C は GNU statement expression で operand / argument の左→右評価を固定する。
strict f32 build では `-ffp-contract=off`、`-fwrapv`、
`-fexcess-precision=standard` を必須とする。

## Library heap lifetime

`--lib` exports `tcs_lib_init()` and `tcs_entry_CLASS_METHOD()` entry points.
The generated runtime uses a non-moving, precise mark-and-sweep collector.
Static fields are roots; generated tracers follow class fields, embedded structs,
collection elements and captured closure cells. Numeric buffers and strings are
not scanned for pointers. Unreachable cycles are reclaimed.

Collection runs only after the outermost exported call returns, when managed
heap bytes exceed twice the previous live heap plus 128 KiB. A host can also call
`tcs_lib_collect()` between calls. Collection during generated code execution is
rejected: locals and expression temporaries are not registered as roots. This
means a single long-running entry point can accumulate garbage until it returns.
Executable `Main()` output has no intermediate automatic collection.

The host must not retain generated heap pointers across these boundaries.
`tcs_lib_heap_bytes()` reports allocated payload plus collector headers;
`tcs_lib_heap_objects()` reports allocation count, including backing buffers.
These are managed-heap metrics, not total process or WebAssembly memory usage.
Collection is synchronous and has no pause-time bound.

Run `bash tcs2c/verify-gc.sh` to check retained graphs, cyclic garbage and bounded
heap growth across repeated host calls. The generated C and `tests/gc-host.c`
can also be linked with Emscripten to exercise the same checks in WebAssembly.

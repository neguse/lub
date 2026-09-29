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

## Array, argument and numeric operations

Closed instantiations of top-level generic classes are specialized before IL
export. Each instantiation has its own fields and static storage; constrained
base classes, arrays and factory lambdas retain their concrete types. Abstract
method declarations participate in virtual dispatch. Open, nested and partial
generic classes are not executable C types. Specialization is limited to 256
closed classes and 128 expansion rounds.

Array initializers use fixed-length storage, including nested and empty arrays.
Constructor and method calls can omit trailing optional arguments. Literal and
local declaration types are retained in IL so an integral initializer does not
turn a float variable into an integer. Numeric casts to `int` truncate toward
zero and fault outside the finite i32 range.

`Math.Sin`, `Cos`, `Sqrt`, `Floor`, `Ceiling`, `Atan2`, `Pow`, `Abs`, `Min`, `Max`
and floating remainder lower to C numeric operations. Link with `-lm` when the
C toolchain requires it. `String.IndexOf` and `Substring` use the Lua runtime's
byte offsets and slicing rules. Run `bash tcs2c/verify-game-core.sh` for these
array, argument, numeric and string contracts.

## Library heap lifetime

`--ref STUB.cs` imports the static methods, static fields, enum values and data
classes used by the input. Stub method bodies are never compiled. The generated
C declares `tcs_host_` functions with the Lua path's dots replaced by underscores;
static fields are exposed as no-argument getters. A host adapter can include the
generated library C and implement these declarations, as in `tests/foreign-host.c`.
External data objects carry an untraced `uint64_t host_value` for native handle
bits. Managed fields and return values still use generated allocation and tracing.

The adapter may borrow managed pointers only during a call; it must not retain
them across a collection boundary. Copy borrowed native strings into managed
strings before returning. Void methods can have typed `out` locals; out calls
with a separate return value and overloaded foreign names are rejected.
Nullable numeric/bool arguments carry either null or a typed managed scalar box.
`--lib` entry points accept `int`, `float` and `bool` arguments and return void.
Run `bash tcs2c/verify-host.sh` for the host boundary contract.

`object` values can contain class, string, collection and delegate references,
or boxed `int`, `float` and `bool` values. Reference conversions preserve object
identity; numeric boxes are separate managed allocations. Runtime tags check
unboxing and reference casts, including collection element types. Interface
method calls dispatch to the implementing class, including inherited methods.
Interface properties and default method bodies are outside this C slice.

Object and interface references participate in the same precise root tracing as
typed class references. Run `bash tcs2c/verify-object-values.sh` to check mixed
roots, cyclic references, interface dispatch, collection and rejected casts.

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

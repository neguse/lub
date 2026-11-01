using System.Collections.Immutable;

namespace TinyCs;

// IL→C backend (tcs2c) 向けの入力契約。検査済みプログラムの
// IL (doc/il-spec.md) と migration metadata (il-spec §14) を、Lua 出力を
// 経由せずに公開する。契約の正本は doc/il-reference.md。

/// <summary>class / record class の migration metadata (il-spec §14) と
/// 骨格 IL。Ctors は explicit constructor (無ければ空 — default 初期化のみ)。custom property の accessor は get_/set_ 名の IlMethodInfo として
/// Methods に現れる。IsRecord の class は positional parameter が Fields の
/// 先頭に並び、Ctors の 1 つがそれらへの代入 (positional ctor) になる。== / != は
/// 構造等価 (backend が field 比較を生成)、with は shallow copy。</summary>
public sealed record IlClassInfo(
    string Name,
    string? BaseName,
    ImmutableArray<IlFieldInfo> Fields,
    string LayoutHash,
    ImmutableArray<IlMethodInfo> Methods,
    ImmutableArray<IlCtorInfo> Ctors = default,
    bool IsRecord = false,
    ImmutableArray<string> Interfaces = default,
    bool IsInterface = false,
    bool IsExternal = false,
    string? DisplayName = null);

/// <summary>explicit constructor。構築順は base ctor → 自 class の field
/// default/initializer → Body (Lua backend と同順)。BaseArgs は base(...)
/// 初期化子の引数 IL (無指定の暗黙 base() は空配列)。Name は呼び出し側の
/// IlNewObj.Ctor / IlCall("S.Name") と同じ名前 (LuaNaming.CtorName)。</summary>
public sealed record IlCtorInfo(
    ImmutableArray<string> Parameters,
    ImmutableArray<string> ParameterTypes,
    IlBlock? Body,
    ImmutableArray<IlExpr> BaseArgs = default,
    ImmutableArray<IlExpr?> ParameterDefaults = default,
    string Name = "new");

/// <summary>InitHash は initializer の hash (無ければ null、IlExporter.InitializerHash)。
/// hot reload が既存 static の initializer 変更を restart と判定するのに使う。</summary>
public sealed record IlFieldInfo(string Name, string Type, bool IsStatic,
    IlExpr? Init = null, string? InitHash = null);

/// <summary>method body の IL。Body が null なら IL 未対応 (診断構文等) で
/// backend は対象外にできる。OutParameters は Parameters のうち out の名前で、
/// 呼び出しは out を除いた引数で IlMultiAssign([戻り値?, out...], [call]) になる。</summary>
public sealed record IlMethodInfo(
    string Name,
    bool IsStatic,
    ImmutableArray<string> Parameters,
    IlBlock? Body,
    string ReturnType = "void",
    ImmutableArray<string> ParameterTypes = default,
    ImmutableArray<IlExpr?> ParameterDefaults = default,
    bool IsAbstract = false,
    ImmutableArray<string> OutParameters = default);

/// <summary>struct / record struct の契約 (il-spec §10)。Fields は instance
/// field (auto property / record struct の positional parameter 込み)。
/// LayoutHash は class と同じ展開規則で、struct 値は reload 時に owner 経由で
/// 再直列化される (il-design §6)。Methods は method、custom property accessor、
/// operator で、instance の呼び出し側 IL は IlCall("S.M", [receiver, args...]) の
/// 静的ディスパッチ (receiver は変数なら place、rvalue なら copy)。static は
/// IsStatic の IlMethodInfo で、receiver が無いことだけが違う。StaticFields は
/// static field と static auto property で、値の layout には入らない。Ctors は explicit ctor (record struct
/// は positional 代入) で IlCall("S.ctor", args) (複数なら CtorName の名前)。`new S()` は IlNewObj の
/// zero 値で Init も走らない (C# の struct 意味論)。IsRecord の ==/!= は
/// IlCall("S.op_Equality", [a, b]) の構造等価、with は IlWith。</summary>
public sealed record IlStructInfo(
    string Name,
    ImmutableArray<IlFieldInfo> Fields,
    string LayoutHash,
    ImmutableArray<IlMethodInfo> Methods = default,
    ImmutableArray<IlCtorInfo> Ctors = default,
    bool IsRecord = false,
    string? DisplayName = null,
    ImmutableArray<IlFieldInfo> StaticFields = default);

/// <summary>enum の定数表。member 名は C# の識別子のままで、IL の
/// IlField(IlVar(enum 名), member 名) と一致する。</summary>
public sealed record IlEnumInfo(
    string Name,
    ImmutableArray<(string Name, int Value)> Members,
    string? DisplayName = null);

/// <summary>結果。TopLevel は top-level 文 (エントリポイント本文相当) の IL
/// (無ければ null、IL 未対応構文を含めば null — Diagnostics で判別)。
/// Enums は enum 名 (hot reload の default 判定用)、EnumTypes は定数表
/// (C backend の型付け用)。</summary>
public sealed record IlExportResult(
    ImmutableArray<IlClassInfo> Classes,
    ImmutableArray<string> Diagnostics,
    IlBlock? TopLevel = null,
    ImmutableArray<IlStructInfo> Structs = default,
    ImmutableArray<string> Enums = default,
    ImmutableArray<IlEnumInfo> EnumTypes = default,
    ImmutableArray<IlForeignMethod> ForeignMethods = default,
    ImmutableArray<IlForeignValue> ForeignValues = default);

/// <summary>--ref の型 (host が持つ実装) の method と static な値。</summary>
public sealed record IlForeignParameter(string Name, string Type, bool IsOut, IlExpr? Default);
public sealed record IlForeignMethod(string Name, string ReturnType,
    ImmutableArray<IlForeignParameter> Parameters, string? Receiver = null);
public sealed record IlForeignValue(string Name, string Type, int? Constant = null);

public static class IlExport
{
    // C backend (tcs2c) と hot reload の判定の入力。compiler はセルフホストの実装 (SelfHostIlExport)
    public static IlExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null) =>
        SelfHostIlExport.Export(csharpSources, specializeGenerics, referenceSources, sourcePaths);
}

namespace TinyCs.Vm;

internal sealed class VmException(string message) : Exception(message);

/// <summary>register / static / 葉の種類 (doc/vm-design.md §6.1)。tcsvm_ops.h の TVM_K_* と同じ値。</summary>
internal enum Kind : byte { I32 = 0, F32 = 1, Bool = 2, Ref = 3 }

/// <summary>emitter の静的型。IL の型文字列 (display 名) から作る。</summary>
internal abstract record VType
{
    public static readonly VType Int = new PrimT(Kind.I32, "int");
    public static readonly VType Float = new PrimT(Kind.F32, "float");
    public static readonly VType Bool = new PrimT(Kind.Bool, "bool");
    public static readonly VType Void = new VoidT();
    public static readonly VType Null = new NullT();
    public static readonly VType String = new StringT();

    /// <summary>heap の object を指す ref の型か。</summary>
    public bool IsRef => this is ClassT or ArrayT or StringT or ListT or DictT or FuncT or CellT;
}

internal sealed record PrimT(Kind Kind, string Name) : VType
{
    public override string ToString() => Name;
}

internal sealed record ClassT(string Name) : VType
{
    public override string ToString() => Name;
}

internal sealed record StructT(string Name) : VType
{
    public override string ToString() => Name;
}

internal sealed record ArrayT(VType Elem) : VType
{
    public override string ToString() => Elem + "[]";
}

internal sealed record StringT : VType
{
    public override string ToString() => "string";
}

internal sealed record ListT(VType Elem) : VType
{
    public override string ToString() => $"List<{Elem}>";
}

internal sealed record DictT(VType Key, VType Value) : VType
{
    public override string ToString() => $"Dictionary<{Key}, {Value}>";
}

/// <summary>delegate (Func / Action)。Ret は Action なら Void。等価は表記で比べる。</summary>
internal sealed record FuncT(IReadOnlyList<VType> Params, VType Ret) : VType
{
    public override string ToString() =>
        Ret is VoidT ? $"Action<{string.Join(", ", Params)}>" : $"Func<{string.Join(", ", Params.Append(Ret))}>";

    public bool Equals(FuncT? other) => other is not null && ToString() == other.ToString();

    public override int GetHashCode() => ToString().GetHashCode();
}

/// <summary>closure が捕捉する変数の箱 (il-spec §7: closure は変数を捕捉する)。VM では葉が Inner の class。</summary>
internal sealed record CellT(VType Inner) : VType
{
    public override string ToString() => $"cell<{Inner}>";
}

internal sealed record VoidT : VType
{
    public override string ToString() => "void";
}

internal sealed record NullT : VType
{
    public override string ToString() => "null";
}

/// <summary>値の葉 (struct は field を再帰的に展開した列)。Type は葉自身の型 (prim か ref)。</summary>
internal sealed record Leaf(string Path, VType Type)
{
    public Kind Kind => Type is PrimT p ? p.Kind : Kind.Ref;
}

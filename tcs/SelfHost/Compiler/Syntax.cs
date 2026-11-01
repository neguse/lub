using System.Collections.Generic;

namespace TcsSelf;

public enum NodeKind
{
    // 宣言
    CompilationUnit,
    Using,
    Namespace,
    TypeDecl,
    EnumMember,
    Field,
    VarDeclarator,
    Property,
    Accessor,
    Method,
    Ctor,
    OperatorDecl,
    Param,
    Attribute,

    // 型
    TypeName,
    PredefType,
    ArrayType,
    NullableType,
    TupleType,

    // 文
    Block,
    LocalDecl,
    DeconstructDecl,
    // (a, var b, (c, d)) = expr: List = 分解の先 (式、PatDecl、PatDiscard、入れ子の DeconstructAssign)、A = 値
    DeconstructAssign,
    ExprStmt,
    If,
    While,
    DoWhile,
    For,
    Foreach,
    Break,
    Continue,
    Return,
    Switch,
    SwitchSection,
    CaseLabel,
    DefaultLabel,
    Empty,
    Unsupported,

    // 式
    IntLit,
    FloatLit,
    StringLit,
    CharLit,
    BoolLit,
    NullLit,
    DefaultLit,
    InterpString,
    InterpHole,
    Name,
    Member,
    Call,
    Arg,
    Index,
    New,
    NewArray,
    ArrayInit,
    ObjectInit,
    CollectionInit,
    MemberInit,
    IndexInit,
    CollectionExpr,
    Unary,
    PostIncDec,
    PreIncDec,
    Binary,
    Assign,
    Conditional,
    Is,
    As,
    Cast,
    Lambda,
    This,
    SwitchExpr,
    SwitchArm,
    Tuple,
    Paren,
    Typeof,
    Checked,
    // Binder が足す変換: A = 値、Info = ConvKind、Type = 変換先
    Conv,

    // pattern
    PatConst,
    PatType,
    PatDecl,
    PatVar,
    PatDiscard,
    PatRel,
    PatNot,
    PatAnd,
    PatOr,
    PatProp,
    PropSub,
}

// 修飾子と引数の修飾の bit
public static class Mods
{
    public const int Public = 1;
    public const int Private = 2;
    public const int Protected = 4;
    public const int Internal = 8;
    public const int Static = 16;
    public const int Readonly = 32;
    public const int Const = 64;
    public const int Sealed = 128;
    public const int Abstract = 256;
    public const int Override = 512;
    public const int Virtual = 1024;
    public const int Partial = 2048;
    public const int New = 4096;
    public const int Extern = 8192;
    public const int Out = 16384;
    public const int Ref = 32768;
    public const int In = 65536;
    public const int This = 131072;
    public const int Params = 262144;
    public const int Required = 524288;
    public const int Unsafe = 1048576;
    public const int Async = 2097152;
    public const int Volatile = 4194304;
    public const int Implicit = 8388608;
    public const int Explicit = 16777216;
    public const int Event = 33554432;
    // delegate の型の宣言 (サブセットの外。名前だけを class として宣言し、型の検査の診断に使う)
    public const int Delegate = 67108864;
}

// 型宣言の種類 (TypeDecl の Value)
public static class DeclKinds
{
    public const int Class = 0;
    public const int Struct = 1;
    public const int Interface = 2;
    public const int Enum = 3;
    public const int RecordClass = 4;
    public const int RecordStruct = 5;
}

// 構文木のノード。種類ごとに class を分けず、1 つの class の slot を種類ごとの意味で使う。
// 各種類の slot の意味は Parser の各メソッドの先頭のコメントにある。
public sealed class Node
{
    public NodeKind Kind;
    public SourceFile File;
    public int Pos;
    public int End;
    public string Text = "";
    // ?. の鎖の中で、この受け手を評価済みの値を持つ Lua の名前 (emitter が使う)
    public string? CondRecv;
    public int Value;
    public int Flags;
    public Node? A;
    public Node? B;
    public Node? C;
    public Node? D;
    public List<Node>? List;
    public List<Node>? List2;
    public List<Node>? List3;

    // 束縛の結果 (Binder が書く)
    public TypeSym? Type;
    public Symbol? Sym;
    // 定数の値 (Binder が畳んだもの)。HasConst のときだけ意味を持つ
    public bool HasConst;
    public ConstValue? Const;
    // 束縛の補足 (種類ごとの意味は Binder を参照)
    public int Info;
    public TypeSym? Type2;
    public Node? Bound;
    public List<TypeSym>? TypeArgs;
    public LocalSym? Local;
    // method group の候補
    public List<Symbol>? Cands;
    // ?. の鎖の中の式 (受け手が null なら鎖全体が null)。CondTop は鎖の最も外の式。
    // 鎖の中の式の Type は持ち上げた型 (値型は T?)、Type2 は持ち上げる前の型
    public bool CondChain;
    // null と値型の腕の条件式 (自然な型を持たず、変換先の型へ各腕を変換する。target-typed)
    public bool TargetTyped;
    public bool CondTop;
    // 拡張メソッドの method group
    public bool ExtGroup;
    // 文が式の中で宣言する局所変数 (out var と pattern の変数)。Emitter が文の前に宣言する
    public List<LocalSym>? DeclLocals;
    // 型の宣言 (TypeDecl) の symbol (Declarer が書く)
    public TypeDeclSym? TypeDecl;
    // この構文から作った IL の位置 (同じ構文の IL は同じ IlPos を共有する)
    public IlPos? IlOrigin;

    public Node(NodeKind kind, SourceFile file, int pos)
    {
        Kind = kind;
        File = file;
        Pos = pos;
    }

    public void Add(Node n)
    {
        if (List == null) List = new List<Node>();
        List.Add(n);
    }

    public void Add2(Node n)
    {
        if (List2 == null) List2 = new List<Node>();
        List2.Add(n);
    }

    public void Add3(Node n)
    {
        if (List3 == null) List3 = new List<Node>();
        List3.Add(n);
    }

    public int Count => List == null ? 0 : List.Count;

    public int Count2 => List2 == null ? 0 : List2.Count;

    public int Count3 => List3 == null ? 0 : List3.Count;

    public Node Item(int i) => List![i];

    public Node Item2(int i) => List2![i];

    public Node Item3(int i) => List3![i];

    public bool Has(int flag) => (Flags & flag) != 0;

    // parse したままの木を複製する (束縛は木を書き換えるので、session は複製を束縛して元の木を次の compile に残す)
    // CloneTree の複製の元の node と、束縛した複製 (generic の単相化が束縛の結果を元の木から引く)
    public Node? Orig;
    public Node? Mirror;
    // 単相化が差し込んだ型の構文 (Roslyn では null 許容の文脈を持たない oblivious な型)
    public bool Oblivious;
    // 単相化が型やファイルの終わりに足した閉じた copy の順番 (1 から)。走査は元の位置でなくこの順で copy を後ろに置く
    public int Appended;

    public Node CloneTree()
    {
        var n = new Node(Kind, File, Pos);
        n.Orig = this;
        n.Oblivious = Oblivious;
        n.Appended = Appended;
        n.End = End;
        n.Text = Text;
        n.Value = Value;
        n.Flags = Flags;
        n.Info = Info;
        n.HasConst = HasConst;
        n.Const = Const;
        if (A != null) n.A = A.CloneTree();
        if (B != null) n.B = B.CloneTree();
        if (C != null) n.C = C.CloneTree();
        if (D != null) n.D = D.CloneTree();
        n.List = CloneList(List);
        n.List2 = CloneList(List2);
        n.List3 = CloneList(List3);
        return n;
    }

    private static List<Node>? CloneList(List<Node>? list)
    {
        if (list == null) return null;
        var copy = new List<Node>();
        foreach (var x in list) copy.Add(x.CloneTree());
        return copy;
    }
}

using System.Collections.Generic;

namespace Tcs;

public enum NodeKind
{
    // Declarations
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

    // Types
    TypeName,
    PredefType,
    ArrayType,
    NullableType,
    TupleType,

    // Statements
    Block,
    LocalDecl,
    DeconstructDecl,
    // (a, var b, (c, d)) = expr: List = deconstruction targets (expressions, PatDecl, PatDiscard, nested DeconstructAssign), A = value
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

    // Expressions
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
    // Conversion added by the Binder: A = value, Info = ConvKind, Type = target
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

// Bits for modifiers and argument modifiers
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
    // Delegate type declaration (outside the subset; only the name is declared as a class, used for type-checking diagnostics)
    public const int Delegate = 67108864;
}

// Kinds of type declarations (the Value of TypeDecl)
public static class DeclKinds
{
    public const int Class = 0;
    public const int Struct = 1;
    public const int Interface = 2;
    public const int Enum = 3;
    public const int RecordClass = 4;
    public const int RecordStruct = 5;
}

// A syntax tree node. Instead of a class per kind, the slots of one class are used with a meaning per kind.
// The meaning of each kind's slots is in the comment at the top of each Parser method.
public sealed class Node
{
    public NodeKind Kind;
    public SourceFile File;
    public int Pos;
    public int End;
    public string Text = "";
    // Inside a ?. chain, the name of the local that holds the already-evaluated value of this receiver
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

    // Binding results (written by the Binder)
    public TypeSym? Type;
    public Symbol? Sym;
    // Constant value (folded by the Binder). Meaningful only when HasConst
    public bool HasConst;
    public ConstValue? Const;
    // Extra binding info (see the Binder for its meaning per kind)
    public int Info;
    public TypeSym? Type2;
    public Node? Bound;
    public List<TypeSym>? TypeArgs;
    public LocalSym? Local;
    // Method group candidates
    public List<Symbol>? Cands;
    // An expression inside a ?. chain (if the receiver is null, the whole chain is null). CondTop is the outermost expression of the chain.
    // For expressions inside the chain, Type is the lifted type (T? for value types) and Type2 is the type before lifting
    public bool CondChain;
    // A conditional expression with null and value-type arms (it has no natural type, and each arm is converted to the target type; target-typed)
    public bool TargetTyped;
    public bool CondTop;
    // Method group of extension methods
    public bool ExtGroup;
    // Locals that a statement declares inside expressions (out var and pattern variables). The Emitter declares them before the statement
    public List<LocalSym>? DeclLocals;
    // The symbol of a type declaration (TypeDecl) (written by the Declarer)
    public TypeDeclSym? TypeDecl;
    // The position of IL built from this syntax (IL from the same syntax shares the same IlPos)
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

    // Copies the tree as parsed (binding modifies the tree, so the session binds the copy and keeps the original tree for the next compile)
    // The original node of a CloneTree copy, and the bound copy (generic monomorphization looks up binding results from the original tree)
    public Node? Orig;
    public Node? Mirror;
    // Type syntax inserted by monomorphization (an oblivious type with no nullable context, in Roslyn terms)
    public bool Oblivious;
    // The order (from 1) of closed copies that monomorphization added at the end of a type or file. Traversal places copies at the end in this order rather than by original position
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

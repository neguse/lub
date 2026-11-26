using System.Collections.Generic;

namespace Tcs;

// TinyC# IL (docs/tcs/il-spec.md). Intermediate representation of method bodies.
// The builder makes all semantic decisions, and the backends (VmEmitter and CEmitter) emit output from the IL alone.
// Operation nodes are type-resolved and monomorphic (il-spec §4).
// Origin is a position for VM line info and --trace, and does not affect semantics.
// Instead of inheritance, nodes are interfaces (IlExpr / IlStat) and the records implementing them, read by branching on type.

// C# position (file and line). IL built from the same syntax shares the same IlPos, and --trace compares by identity (it is a class, so == is identity)
public sealed class IlPos
{
    public string File;
    public int Line;
    // 1-based column (UTF-8 bytes; 0 if unknown)
    public int Col;

    public IlPos(string file, int line)
    {
        File = file;
        Line = line;
    }
}

public interface IlExpr
{
    IlPos? Origin { get; }
}

public interface IlStat
{
    IlPos? Origin { get; }
}

// ---- Expressions ----

// Literal kinds
public enum IlLitKind
{
    Null,
    Bool,
    Int,
    Float,
    String,
}

// Literal value. Kind determines which member to read: I for Bool and Int (bool is 0 / 1, char is the code unit, uint is the int with the same 32 bits),
// F for Float (NaN and ±∞ are kept as values), S for String (the raw value without escapes). Construction and display are in IlLiterals
public record IlLit(IlLitKind Kind, int I, float F, string S, IlPos? Origin) : IlExpr;

// Reference to a local / parameter / type name
public record IlVar(string Name, IlPos? Origin) : IlExpr;

// Place for reading and writing a field: recv.Name
public record IlField(IlExpr Recv, string Name, IlPos? Origin) : IlExpr;

// Element place: recv[idx]. PlusOne means a List / array index (idx stays 0-based as in C#; out of range faults), false means a Dictionary key.
// Unchecked (an array whose elements may be null) is not read by the VM and C backends (the builder uses it to decide whether the expression may be re-read)
public record IlIndex(IlExpr Recv, IlExpr Idx, bool PlusOne, bool Unchecked, IlPos? Origin) : IlExpr;

// Length (List.Count / string.Length / array.Length)
public record IlLen(IlExpr E, IlPos? Origin) : IlExpr;

public enum IlBinOp
{
    AddNum,
    Concat,
    Sub,
    Mul,
    DivNum,
    RemNum,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
}

public record IlBin(IlBinOp Op, IlExpr L, IlExpr R, IlPos? Origin) : IlExpr;

public enum IlUnOp
{
    Neg,
    Not,
    BitNot,
    ToFloat,
}

public record IlUn(IlUnOp Op, IlExpr E, IlPos? Origin) : IlExpr;

public record IlParen(IlExpr E, IlPos? Origin) : IlExpr;

// Conditional expression (cond ? t : f)
public record IlTernary(IlExpr Cond, IlExpr T, IlExpr F, IlPos? Origin) : IlExpr;

// Call by resolved callee name: Callee(args).
// callee is "Math.Min" / "List.Add" / "__tcs_idiv" / "__tcs_writeline" etc. (docs/tcs/il-reference.md)
public record IlCall(string Callee, List<IlExpr> Args, IlPos? Origin) : IlExpr;

// Call of an expression callee: callee(args)
public record IlDynCall(IlExpr Callee, List<IlExpr> Args, IlPos? Origin) : IlExpr;

// Instance method call: recv.Method(args)
public record IlInvoke(IlExpr Recv, string Method, List<IlExpr> Args, IlPos? Origin) : IlExpr;

// Class creation: TypeName.Ctor(args)
public record IlNewObj(string TypeName, List<IlExpr> Args, string Ctor, IlPos? Origin) : IlExpr;

// Entry of a table construction. [k]=v if there is a Key, name=v if there is a NameKey, an array entry if neither
public record IlTableEntry(IlExpr? Key, IlExpr Value, string? NameKey);

// Table construction (List / Dictionary literals, option tables of reference types).
// ElementType is the element type of an array / List literal (for the C backend)
public record IlTable(List<IlTableEntry> Entries, string? ElementType, string? KeyType, bool IsArray, string? ObjectType, IlPos? Origin) : IlExpr;

// Fixed-length array `new T[n]`. Elements are T's default value (Default: its IL for value types, the IlNewObj zero value for structs, null for reference types)
public record IlNewArray(string ElementType, IlExpr Length, IlExpr? Default, IlPos? Origin) : IlExpr;

// Creation of an empty Queue / HashSet. Type is the type's display name (element type for the C backend)
public record IlNewQueue(string Type, IlPos? Origin) : IlExpr;

public record IlNewHashSet(string Type, IlPos? Origin) : IlExpr;

// Numeric conversion (TargetType is int / float). From an object / type argument value it is an unbox, and it faults if the runtime type differs.
// Unbox marks a conversion from an object / type argument value (it can fault in the runtime type check). The VM and C backends do not read it and decide from Value's type
public record IlNumericConvert(IlExpr Value, string TargetType, bool Unbox, IlPos? Origin) : IlExpr;

// Cast of a reference value. The VM and C backends build the runtime type check from TargetType
public record IlRefCast(IlExpr Value, string TargetType, IlPos? Origin) : IlExpr;

// Class type test: T or a class derived from it (il-spec §9)
public record IlIsType(IlExpr E, string TypeRef, bool IsInterface, IlPos? Origin) : IlExpr;

// Type for primitive type tests. Number is either int or float (or enum)
public enum IlPrimType
{
    Number,
    Int,
    Float,
    Bool,
    String,
}

// Primitive type test of the value inside an object
public record IlIsPrimType(IlExpr E, IlPrimType Type, IlPos? Origin) : IlExpr;

// Sequential execution in expression position: runs a statement list in order, and the return value is the expression's value (a GNU statement expression in C). The statements inside are limited to local / assignment / call / return / if / for-pairs
public record IlIife(List<IlStat> Stats, IlPos? Origin) : IlExpr;

// Closure. Either ExprBody or Body. PatternLocals declares the is-pattern designations inside an expression body at the start of the function
public record IlClosure(List<string> Params, IlBlock? Body, IlExpr? ExprBody, List<string> PatternLocals, IlPos? Origin) : IlExpr;

// Method group of a static method (conversion to a delegate)
public record IlMethodGroup(string TypeRef, string Method, IlPos? Origin) : IlExpr;

// Copy point of a struct value (il-spec §10).
// Nullable means E is a struct T?, and the inner struct is copied only when it has a value
public record IlStructCopy(IlExpr E, string TypeName, bool Nullable, IlPos? Origin) : IlExpr;

// ---- Nullable<T> (il-spec §3 / §13). In the VM, a has-value register and a value register; in C, a { has, v } struct ----

// Implicit conversion T → T?. Type is T's display name
public record IlNullableWrap(IlExpr E, string Type, IlPos? Origin) : IlExpr;

// HasValue / `!= null` / `is not null` (bool)
public record IlNullableHasValue(IlExpr E, IlPos? Origin) : IlExpr;

// .Value. Faults if there is no value
public record IlNullableValue(IlExpr E, IlPos? Origin) : IlExpr;

// `e ?? d` / GetValueOrDefault(d). d is evaluated only when e has no value
public record IlNullableGetOrDefault(IlExpr E, IlExpr Default, IlPos? Origin) : IlExpr;

// Lifted operators (C# §12.4.8). Both operands are T?
public enum IlLiftedOp
{
    Add,
    Sub,
    Mul,
    DivFloat,
    DivInt,
    RemFloat,
    RemInt,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
}

public record IlLiftedBin(IlLiftedOp Op, IlExpr L, IlExpr R, IlPos? Origin) : IlExpr;

// Lifted unary (Neg / Not / BitNot / ToFloat). No value if the operand has no value
public record IlLiftedUn(IlUnOp Op, IlExpr E, IlPos? Origin) : IlExpr;

// Explicit downcast of a class reference `(T)e` (il-spec §9)
public record IlCast(IlExpr E, string TypeRef, IlPos? Origin) : IlExpr;

// How to build the zero value passed as the default value of a type argument instantiated with a struct. HasToString means the ToString override is used for stringification.
// The hidden argument for type argument defaults appears only in non-monomorphized IL, and the VM and C backends do not read it (C treats IlStructDescriptor as an error)
public record IlStructDescriptor(string TypeName, List<IlExpr> NewArgs, bool HasToString, IlPos? Origin) : IlExpr;

// Override entry of a with expression
public record IlOverride(string Name, IlExpr Value);

// Record with expression (shallow copy and overrides). If Src is an IlStructCopy, it is already copied (with on a struct)
public record IlWith(IlExpr Src, List<IlOverride> Overrides, IlPos? Origin) : IlExpr;

// ---- Statements ----

public record IlBlock(List<IlStat> Stats);

// Variable introduction. Type is the declared type (a display string; set even when there is an Init)
public record IlLocal(string Name, IlExpr? Init, string? Type, IlPos? Origin) : IlStat;

public record IlAssign(IlExpr Target, IlExpr Value, IlPos? Origin) : IlStat;

public record IlCallStat(IlExpr Call, IlPos? Origin) : IlStat;

// One arm of an if
public record IlArm(IlExpr Cond, IlBlock Body);

public record IlIf(List<IlArm> Arms, IlBlock? Else, IlPos? Origin) : IlStat;

// while. Trailer is the increment from desugaring a for (after the continue label)
public record IlWhile(IlExpr Cond, IlBlock Body, IlBlock? Trailer, IlPos? Origin) : IlStat;

// do-while (evaluates Cond after the body and repeats if true)
public record IlRepeat(IlBlock Body, IlExpr Cond, IlPos? Origin) : IlStat;

// Numeric for: sets the int Var to Start and, while Var < Limit (Var <= Limit when Inclusive), repeats, incrementing Var by 1 (wrapping) after the body.
// Limit is evaluated only once, after Start. The builder creates it only when it knows that Limit does not change during the for and that the body neither writes to Var nor captures it
public record IlNumericFor(string Var, IlExpr Start, IlExpr Limit, bool Inclusive, IlBlock Body, IlPos? Origin) : IlStat;

// foreach (List / array)
public record IlForeachList(string Var, IlExpr Coll, IlBlock Body, IlPos? Origin) : IlStat;

// foreach (Dictionary). The iteration variable is a KeyValuePair
public record IlForeachDict(string Var, IlExpr Coll, IlBlock Body, IlPos? Origin) : IlStat;

// foreach over string.EnumerateRunes() (Var is the integer code point of each rune; decoding is in runtime/utf8)
public record IlForeachRunes(string Var, IlExpr Str, IlBlock Body, IlPos? Origin) : IlStat;

public record IlBreak(IlPos? Origin) : IlStat;

public record IlContinue(IlPos? Origin) : IlStat;

public record IlReturn(IlExpr? Value, IlPos? Origin) : IlStat;

// Block scope (closes temp locals)
public record IlDo(IlBlock Body, IlPos? Origin) : IlStat;

// Break scope: an IlBreak in the body (not bound to a loop) exits here (early break in a switch statement)
public record IlBreakScope(IlBlock Body, IlPos? Origin) : IlStat;

// Multiple assignment: t1, t2 = v1, v2 (deconstruction, and the return value and out values of a call with out arguments). If Declare, the targets are declared as locals
public record IlMultiAssign(List<IlExpr> Targets, List<IlExpr> Values, bool Declare, IlPos? Origin) : IlStat;

// Key loop: iterates with the keys (and values) of Coll in KVar (and VVar).
// The builder only creates it in the List.Clear / Dictionary.Clear shape (setting every key to null), and the VM and C backends turn it into a runtime clear
public record IlForPairs(string KVar, string? VVar, IlExpr Coll, IlBlock Body, IlPos? Origin) : IlStat;

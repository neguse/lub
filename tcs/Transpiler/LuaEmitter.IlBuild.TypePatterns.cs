using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// IL builder: pattern の型照合と designation の束縛
public partial class LuaEmitter
{
    // legacy EmitTypeCheck の写像。struct の入力 (`S` / `S?`) を同じ struct 型で
    // 照合するのは値の有無だけ (struct の値は型 tag を持たない)
    private IlExpr? BuildTypeCheck(IlExpr expr, ITypeSymbol? inputType,
        ITypeSymbol? patternType, string? typeRef)
    {
        if (LuaTypeNameFor(patternType) is { } luaType)
            return new IlIsLuaType(expr, luaType);
        if (IsStructuralType(patternType))
        {
            if (SymbolEqualityComparer.Default.Equals(inputType, patternType))
                return new IlBin(IlBinOp.Ne, expr, new IlLit("nil"));
            return StructuralTypeTests
                ? new IlIsRefType(expr, patternType!.ToDisplayString()) : null;
        }
        if (patternType is { IsValueType: true } && inputType != null)
        {
            if (SymbolEqualityComparer.Default.Equals(inputType, patternType))
                return BindPatternInput(expr, _ => new IlLit("true"));
            if (IsNullableValueType(inputType) && SymbolEqualityComparer.Default
                    .Equals(UnwrapNullable(inputType), patternType))
                return new IlNullableHasValue(expr);
        }
        return typeRef == null ? null : new IlIsType(expr, typeRef);
    }

    // struct の宣言 pattern の designation に束縛する値: 入力 (`S?` は値が
    // あると判った後の中の値) の copy
    private IlExpr DesignationValue(IlExpr input, ITypeSymbol inputType,
        ITypeSymbol declType)
    {
        var value = IsNullableValueType(inputType) ? new IlNullableValue(input) : input;
        return declType is INamedTypeSymbol { IsReadOnly: true }
            ? value : new IlStructCopy(value, TypeName(declType), Copiers: StructCopiers(declType));
    }

    // 入力 `S` / `S?` に当てた struct `S` の宣言 pattern なら (入力型, S)
    private static (ITypeSymbol, ITypeSymbol)? StructDesignation(SemanticModel model,
        DeclarationPatternSyntax dp) =>
        model.GetTypeInfo(dp.Type).Type is { } declType && IsUserStruct(declType)
        && PatternInputType(model, dp) is { } inputType
        && SymbolEqualityComparer.Default.Equals(UnwrapNullable(inputType), declType)
            ? (inputType, declType) : null;

    // switch の designation の前宣言。class は入力をそのまま束縛する (照合が
    // 外れた arm では使われない)。struct は arm ごとに照合の中で copy を束縛する
    // (同名の designation を持つ arm 同士で値を共有しない) ので型だけ宣言する
    private IlLocal SwitchDesignationLocal(SemanticModel model,
        DeclarationPatternSyntax dp, string name, IlExpr sw) =>
        StructDesignation(model, dp) is (_, var declType)
            ? new IlLocal(name, null, declType.ToDisplayString())
            : new IlLocal(name, sw);

    // `and` の右辺の入力は左辺が絞った型。`S?` の入力を左辺が struct `S` に
    // 絞ったら (値があると判っているので) 右辺は中の値を照合する
    private static IlExpr AndRightInput(SemanticModel model, BinaryPatternSyntax bp,
        IlExpr input) =>
        bp.IsKind(SyntaxKind.AndPattern)
        && model.GetOperation(bp.Left) is IPatternOperation left
        && IsNullableValueType(left.InputType) && IsUserStruct(left.NarrowedType)
            ? new IlNullableValue(input) : input;

    private static bool IsStructuralType(ITypeSymbol? type) =>
        type is IArrayTypeSymbol || type != null
            && (IsListType(type.OriginalDefinition.ToDisplayString())
                || IsDictType(type.OriginalDefinition.ToDisplayString()));

    private static ITypeSymbol? PatternInputType(SemanticModel model,
        SyntaxNode node) => model.GetOperation(node) switch
        {
            IPatternOperation pattern => pattern.InputType,
            IPatternCaseClauseOperation clause => clause.Pattern.InputType,
            _ => null,
        };

    // `x is S v` (x: S / S?): 値があれば copy を束縛して true
    private IlExpr BuildStructDeclarationIs(IlVar target, IlExpr expr,
        ITypeSymbol inputType, ITypeSymbol declType)
    {
        if (!IsNullableValueType(inputType))
            return new IlIife([
                new IlAssign(target, DesignationValue(expr, inputType, declType)),
                new IlReturn(new IlLit("true"))]);
        return BindPatternInput(expr, input => new IlIife([
            new IlIf([(new IlNullableHasValue(input), new IlBlock([
                new IlAssign(target, DesignationValue(input, inputType, declType)),
                new IlReturn(new IlLit("true"))]))], null),
            new IlReturn(new IlLit("false"))]))!;
    }
}

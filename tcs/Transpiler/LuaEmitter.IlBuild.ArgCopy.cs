using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 可変 struct の引数の copy の省略 (Lua だけ。il-spec §10)。引数の copy が守るのは 2 つで、
// 呼ばれた側の書き換えが呼び出し側に見えないことと、呼び出しの間に呼び出し側の場所が
// 変わっても呼ばれた側の値が変わらないこと。前者は呼ばれた側が受け持つ: 書き換える (か
// lambda が捕捉する) parameter だけを入口で copy する。後者は呼び出し側が受け持つ: 呼び出しの間に
// 変わらない場所 (lambda が捕捉しない local / parameter で、後の引数と受け手に現れないもの) だけ
// copy を省く。呼ばれた側の判断を本文の中に閉じるので、本文だけの hot reload で判断が古くならない
public partial class LuaEmitter
{
    // IL を C backend に渡す経路は copy 地点を残す (C は値渡しで copy が要らない)
    internal bool KeepsArgumentCopies { get; init; }

    // 引数の値を場所から写すか省くかを決めて返す。parameters は呼ばれる側の宣言
    private IlExpr WrapArgumentCopy(SemanticModel model, IMethodSymbol? callee,
        IReadOnlyList<ExpressionSyntax> arguments, int index, ExpressionSyntax? receiver,
        IlExpr built) =>
        CanShareArgument(model, callee, arguments, index, receiver)
            ? built
            : WrapStructCopy(model, arguments[index], built);

    private bool CanShareArgument(SemanticModel model, IMethodSymbol? callee,
        IReadOnlyList<ExpressionSyntax> arguments, int index, ExpressionSyntax? receiver)
    {
        if (KeepsArgumentCopies || callee == null) return false;
        var declared = (callee.ReducedFrom ?? callee).OriginalDefinition;
        var offset = callee.ReducedFrom != null ? 1 : 0;
        if (index + offset >= declared.Parameters.Length
            || declared.Parameters[index + offset] is not { RefKind: RefKind.None, IsParams: false } parameter
            || !CopiesOnEntryWhenWritten(parameter.Type)
            || IsReferenceOnlyType(declared.ContainingType)
            || declared.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not
                (MethodDeclarationSyntax or OperatorDeclarationSyntax or ConstructorDeclarationSyntax))
            return false;
        if (arguments[index] is not IdentifierNameSyntax name
            || model.GetSymbolInfo(name).Symbol is not { } variable
            || variable is not (ILocalSymbol or IParameterSymbol))
            return false;
        var scope = name.Ancestors().FirstOrDefault(a =>
            a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax
                or PropertyDeclarationSyntax);
        if (scope == null || IsCapturedByLambda(model, scope, variable)) return false;
        return !arguments.Skip(index + 1).Append(receiver)
            .Any(other => other != null && Mentions(model, other, variable));
    }

    // 入口の copy の対象。generic な struct は呼び出し側だけが型引数の copier を持つので除く
    private static bool CopiesOnEntryWhenWritten(ITypeSymbol? type) =>
        IsMutableStruct(type) && type is INamedTypeSymbol { TypeParameters.Length: 0 };

    private static bool Mentions(SemanticModel model, SyntaxNode node, ISymbol variable) =>
        node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id =>
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, variable));

    private static bool IsCapturedByLambda(SemanticModel model, SyntaxNode scope, ISymbol variable) =>
        scope.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>()
            .Any(fn => Mentions(model, fn, variable));

    // 書き換える / lambda が捕捉する struct の parameter を入口で copy する
    private void EmitParameterEntryCopies(SemanticModel model, SyntaxNode member,
        ParameterListSyntax parameterList)
    {
        if (KeepsArgumentCopies) return;
        foreach (var syntax in parameterList.Parameters)
        {
            // out parameter は呼び出し元の値を受け取らないので copy する値が無い
            if (model.GetDeclaredSymbol(syntax) is not { RefKind: not RefKind.Out } parameter
                || !CopiesOnEntryWhenWritten(parameter.Type)
                || !(IsCapturedByLambda(model, member, parameter) || IsWritten(model, member, parameter)))
                continue;
            var name = syntax.Identifier.ValueText;
            AppendLine($"{name} = {RenderIl(CopyIfMutableStruct(parameter.Type, new IlVar(name)))}");
        }
    }

    private static bool IsWritten(SemanticModel model, SyntaxNode member, IParameterSymbol parameter) =>
        member.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id =>
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, parameter)
            && WritesThroughUse(model, id));

    // parameter の出現が値を書き換え得るか。field の path を struct の中だけ上へ辿る
    // (class や配列を通ったらその先は parameter の値の中ではない)。readonly でない instance member の
    // 呼び出しは receiver を書き換え得るとみなす (本文を見ると、その本文の hot reload で判断が古くなる)
    private static bool WritesThroughUse(SemanticModel model, ExpressionSyntax use)
    {
        var node = use;
        while (true)
        {
            switch (node.Parent)
            {
                case ParenthesizedExpressionSyntax paren:
                    node = paren;
                    continue;
                case MemberAccessExpressionSyntax access when access.Expression == node:
                    switch (model.GetSymbolInfo(access).Symbol)
                    {
                        case IMethodSymbol { ReducedFrom: not null }:
                            return false;
                        case IMethodSymbol method:
                            return !method.IsStatic && !method.IsReadOnly;
                        case IPropertySymbol { GetMethod.IsReadOnly: false } when
                            access.Parent is not AssignmentExpressionSyntax:
                            return true;
                    }
                    if (model.GetTypeInfo(access).Type is not { IsReferenceType: false })
                        return false;
                    node = access;
                    continue;
                case AssignmentExpressionSyntax assign:
                    return assign.Left == node;
                case PrefixUnaryExpressionSyntax prefix:
                    return prefix.Kind() is SyntaxKind.PreIncrementExpression
                        or SyntaxKind.PreDecrementExpression;
                case PostfixUnaryExpressionSyntax postfix:
                    return postfix.Kind() is SyntaxKind.PostIncrementExpression
                        or SyntaxKind.PostDecrementExpression;
                case ArgumentSyntax argument:
                    return !argument.RefKindKeyword.IsKind(SyntaxKind.None)
                        || argument.Parent is TupleExpressionSyntax;
                default:
                    return false;
            }
        }
    }
}

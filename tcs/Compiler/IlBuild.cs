using System.Collections.Generic;

namespace TcsSelf;

// Bound tree → IL. Builds IL from the binding results (the Type / Sym / Info of each Node and the Conv nodes).
// Semantic decisions (integer division, concatenation, indexing, facade resolution) are made here, and unsupported syntax returns null
public sealed class IlBuild
{
    private readonly IlContext Ctx;

    public IlBuild(IlContext ctx)
    {
        Ctx = ctx;
    }

    // A shape that is not mapped. Records it as an internal error (tcs check and the compile stop with the location and reason)
    private IlExpr? Unported(string what, Node at)
    {
        Ctx.Internal("IlBuild: " + what + " (" + at.Kind + " at " + at.File.Path + ":" + at.File.LineOf(at.Pos) + ")");
        return null;
    }

    private bool UnportedStat(string what, Node at)
    {
        Unported(what, at);
        return false;
    }

    // Syntax position (the same node gets the same IlPos)
    public static IlPos Origin(Node node)
    {
        if (node.IlOrigin == null)
        {
            node.IlOrigin = new IlPos(node.File.Path, node.File.LineOf(node.Pos));
            node.IlOrigin.Col = node.File.ColumnOf(node.Pos);
        }
        return node.IlOrigin;
    }

    // ---- Statements ----

    public bool BuildStatsInto(List<Node>? statements, List<IlStat> acc)
    {
        if (statements == null) return true;
        foreach (var stmt in statements)
        {
            if (!BuildStatInto(stmt, acc)) return false;
        }
        return true;
    }

    private IlBlock? BuildBlock(Node stmt)
    {
        var acc = new List<IlStat>();
        bool ok = stmt.Kind == NodeKind.Block ? BuildStatsInto(stmt.List, acc) : BuildStatInto(stmt, acc);
        return ok ? new IlBlock(acc) : null;
    }

    // A reference-type local declared with var gets a nullable annotation in a #nullable enable context (as Roslyn displays it)
    private static TypeSym LocalDisplayType(Node decl, TypeSym t)
    {
        bool isVar = decl.A != null && decl.A.Kind == NodeKind.TypeName && decl.A.Text == "var" && decl.A.A == null && decl.A.List == null;
        if (!isVar) return t;
        // A var in a disabled context has no annotation (the annotation of the initializer's type is dropped too)
        if (!decl.File.NullableEnabled || t.Oblivious) return t.WithoutAnnotation();
        if (t.Annotated) return t;
        if (t.IsValueType && !(t.Kind == TypeKind.TypeParam && !t.IsStructParam) || t.IsError || t.Kind == TypeKind.Null) return t;
        return t.WithAnnotation();
    }

    // Forward declarations for out var / is-pattern designations. Placed before the statement and before a method's expression body (=>)
    private void AddPreDecls(Node node, List<IlStat> acc)
    {
        var origin = Origin(node);
        foreach (var d in CollectPreDecls(node))
        {
            var dl = d.Local;
            // A discard (`out var _`) has no type
            acc.Add(new IlLocal(dl != null ? dl.IlName : "_", null, dl != null && d.Text != "_" ? IlTypes.Display(dl.Type) : null, origin));
        }
    }

    public bool BuildStatInto(Node stmt, List<IlStat> acc)
    {
        var origin = Origin(stmt);
        if (stmt.Kind != NodeKind.Block) AddPreDecls(stmt, acc);
        switch (stmt.Kind)
        {
            case NodeKind.Return:
                {
                    IlExpr? value = null;
                    if (stmt.A != null)
                    {
                        value = BuildExpr(stmt.A);
                        if (value == null) return false;
                        value = WrapStructCopy(stmt.A, value);
                    }
                    AddReturnStat(acc, value, origin);
                    return true;
                }
            case NodeKind.LocalDecl:
                {
                    if (stmt.List == null) return true;
                    foreach (var v in stmt.List)
                    {
                        IlExpr? init = null;
                        if (v.A != null)
                        {
                            init = BuildExpr(v.A);
                            if (init == null) return false;
                            init = WrapStructCopy(v.A, init);
                        }
                        var name = v.Local != null ? v.Local.IlName : v.Text;
                        var declaredType = v.Local != null ? IlTypes.Display(LocalDisplayType(stmt, v.Local.Type)) : null;
                        // A conditional expression in an initializer becomes a statement (same policy as in return position)
                        if (init is IlTernary lt)
                        {
                            var varNode = new IlVar(name, null);
                            acc.Add(new IlLocal(name, null, declaredType, origin));
                            var thenB = new IlBlock(One(new IlAssign(varNode, lt.T, null)));
                            var elseB = new IlBlock(One(new IlAssign(varNode, lt.F, null)));
                            acc.Add(new IlIf(Arm(lt.Cond, thenB), elseB, origin));
                            continue;
                        }
                        if (init is IlIife li)
                        {
                            var shape = TryGetValueChainShape(li);
                            if (shape != null)
                            {
                                var varNode = new IlVar(name, null);
                                acc.Add(new IlLocal(name, null, declaredType, origin));
                                foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                                if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, varNode), origin));
                                else acc.Add(new IlAssign(varNode, shape.Tail!, origin));
                                continue;
                            }
                        }
                        acc.Add(new IlLocal(name, init, declaredType, origin));
                    }
                    return true;
                }
            case NodeKind.ExprStmt:
                return BuildExprStatInto(stmt.A!, stmt, acc);
            case NodeKind.If:
                {
                    var built = BuildIf(stmt);
                    if (built == null) return false;
                    // An IIFE that returns the value of the root condition is hoisted before the if (the root condition is always evaluated first, so the evaluation order does not change.
                    // An elseif condition is evaluated only when the previous conditions are false, so it is excluded)
                    if (built.Arms[0].Cond is IlIife condIife)
                    {
                        var shape = TryGetValueChainShape(condIife);
                        if (shape != null)
                        {
                            var condVar = new IlVar("__tcs_cond" + _condHoist, null);
                            _condHoist++;
                            acc.Add(new IlLocal(condVar.Name, null, "bool", origin));
                            foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                            if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, condVar), origin));
                            else acc.Add(new IlAssign(condVar, shape.Tail!, origin));
                            var arms = new List<IlArm>();
                            arms.Add(new IlArm(condVar, built.Arms[0].Body));
                            for (int i = 1; i < built.Arms.Count; i++) arms.Add(built.Arms[i]);
                            built = built with { Arms = arms };
                        }
                    }
                    acc.Add(built with { Origin = origin });
                    return true;
                }
            case NodeKind.While:
                {
                    var cond = BuildExpr(stmt.A!);
                    var body = BuildBlock(stmt.B!);
                    if (cond == null || body == null) return false;
                    acc.Add(new IlWhile(cond, body, null, origin));
                    return true;
                }
            case NodeKind.Block:
                // Flatten without wrapping in do..end
                return BuildStatsInto(stmt.List, acc);
            case NodeKind.Break:
                acc.Add(new IlBreak(origin));
                return true;
            case NodeKind.Continue:
                acc.Add(new IlContinue(origin));
                return true;
            case NodeKind.DoWhile:
                {
                    var body = BuildBlock(stmt.B!);
                    var cond = BuildExpr(stmt.A!);
                    if (body == null || cond == null) return false;
                    // The condition is evaluated after the body, at its own position (--trace)
                    acc.Add(new IlRepeat(body, WithExprOrigin(cond, Origin(stmt.A!)), origin));
                    return true;
                }
            case NodeKind.Empty:
                return true;
            case NodeKind.DeconstructAssign:
                return BuildDeconstructInto(stmt, stmt.A!, origin, acc);
            case NodeKind.For:
                return BuildForInto(stmt, acc);
            case NodeKind.Foreach:
                return BuildForeachInto(stmt, acc);
            case NodeKind.Switch:
                return BuildSwitchStatInto(stmt, acc);
        }
        return UnportedStat("statement", stmt);
    }

    private IlIf? BuildIf(Node ifStmt)
    {
        var arms = new List<IlArm>();
        IlBlock? elseBlock = null;
        var current = ifStmt;
        while (true)
        {
            var cond = BuildExpr(current.A!);
            var body = BuildBlock(current.B!);
            if (cond == null || body == null) return null;
            // An elseif condition is evaluated at its own position, not at the position of the if statement (--trace)
            arms.Add(new IlArm(current == ifStmt ? cond : WithExprOrigin(cond, Origin(current.A!)), body));
            if (current.C == null) break;
            if (current.C.Kind == NodeKind.If)
            {
                if (CollectPreDecls(current.C).Count > 0)
                {
                    var nested = new List<IlStat>();
                    if (!BuildStatInto(current.C, nested)) return null;
                    elseBlock = new IlBlock(nested);
                    break;
                }
                current = current.C;
                continue;
            }
            elseBlock = BuildBlock(current.C);
            if (elseBlock == null) return null;
            break;
        }
        return new IlIf(arms, elseBlock, null);
    }

    private void AddReturnStat(List<IlStat> acc, IlExpr? value, IlPos? origin)
    {
        if (value is IlTernary ternary)
        {
            var thenB = new IlBlock(One(new IlReturn(ternary.T, null)));
            var elseB = new IlBlock(One(new IlReturn(ternary.F, null)));
            acc.Add(new IlIf(Arm(ternary.Cond, thenB), elseB, origin));
            return;
        }
        // A switch expression is inlined (return works as is). One without an else stays an IIFE to avoid turning into a return without a value
        if (value is IlIife iife)
        {
            var shape = TryGetValueChainShape(iife);
            if (shape != null && shape.Complete)
            {
                foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                if (shape.Chain != null) acc.Add(IlTree.WithOrigin(shape.Chain, origin));
                else acc.Add(new IlReturn(shape.Tail, origin));
                return;
            }
        }
        acc.Add(new IlReturn(value, origin));
    }

    private int _condHoist;

    // Recognizes the shape of a value-returning IIFE. The target is [IlLocal*, IlIf (each arm = leading statements* + a trailing IlReturn, else has the same shape or is the no-match fault of a switch expression)?, IlReturn?]
    // (lowered from switch expressions / ?. / ?? / TryGetValue / GetValueOrDefault). A trailing IlReturn is normalized into the else.
    // Complete = every path returns a value or faults (if false, nil when nothing matches; it cannot be used in return position because it becomes a return without a value)
    private static ValueChain? TryGetValueChainShape(IlIife iife)
    {
        var stats = new List<IlStat>();
        foreach (var st in iife.Stats) stats.Add(st);
        IlReturn? trailing = null;
        if (stats.Count > 0 && stats[stats.Count - 1] is IlReturn tr && tr.Value != null)
        {
            trailing = tr;
            stats.RemoveAt(stats.Count - 1);
        }
        // Linear shape (no branches): [leading declarations*, return v]
        if (trailing != null && LeadingOk(stats, stats.Count))
            return new ValueChain(stats, null, trailing.Value, true);
        if (stats.Count == 0 || !(stats[stats.Count - 1] is IlIf tail)) return null;
        if (!LeadingOk(stats, stats.Count - 1)) return null;
        if (tail.Else != null && trailing != null) return null;
        foreach (var a in tail.Arms)
        {
            if (!IsReturningBlock(a.Body)) return null;
        }
        if (tail.Else != null && !IsReturningBlock(tail.Else) && !IsNoMatchBlock(tail.Else)) return null;
        var setup = new List<IlStat>();
        for (int i = 0; i < stats.Count - 1; i++) setup.Add(stats[i]);
        IlIf chain = tail;
        if (trailing != null) chain = tail with { Else = new IlBlock(One(trailing)) };
        return new ValueChain(setup, chain, null, tail.Else != null || trailing != null);
    }

    // Leading statements are only unconditionally executed declarations, assignments, and calls (hoisting them gives the same result)
    private static bool LeadingOk(List<IlStat> stats, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var st = stats[i];
            if (st is IlLocal || st is IlAssign || st is IlCallStat) continue;
            if (st is IlMultiAssign ma && ma.Declare) continue;
            return false;
        }
        return true;
    }

    private static bool IsReturningBlock(IlBlock block)
    {
        int n = block.Stats.Count;
        if (n == 0) return false;
        if (!(block.Stats[n - 1] is IlReturn r) || r.Value == null) return false;
        for (int i = 0; i < n - 1; i++)
        {
            if (block.Stats[i] is IlReturn) return false;
        }
        return true;
    }

    // The fault when no arm of a switch expression matches (C#'s SwitchExpressionException)
    private static IlBlock NoMatchBlock()
    {
        return new IlBlock(One(new IlCallStat(new IlCall("__tcs_nomatch", new List<IlExpr>(), null), null)));
    }

    private static bool IsNoMatchBlock(IlBlock block)
    {
        return block.Stats.Count == 1 && block.Stats[0] is IlCallStat s && s.Call is IlCall c && c.Callee == "__tcs_nomatch";
    }

    // An if chain where the trailing return of each arm is turned into an assignment to target (the no-match fault is kept as is)
    private static IlIf SwitchChainAsAssigns(IlIf chain, IlExpr target)
    {
        var arms = new List<IlArm>();
        foreach (var a in chain.Arms) arms.Add(new IlArm(a.Cond, RewriteReturnAsAssign(a.Body, target)));
        IlBlock? elseBlock = null;
        if (chain.Else != null) elseBlock = IsNoMatchBlock(chain.Else) ? chain.Else : RewriteReturnAsAssign(chain.Else, target);
        return new IlIf(arms, elseBlock, chain.Origin);
    }

    private static IlBlock RewriteReturnAsAssign(IlBlock body, IlExpr target)
    {
        var stats = new List<IlStat>();
        int n = body.Stats.Count;
        for (int i = 0; i < n - 1; i++) stats.Add(body.Stats[i]);
        stats.Add(new IlAssign(target, ((IlReturn)body.Stats[n - 1]).Value!, null));
        return new IlBlock(stats);
    }

    // The value of an expression body (=>) is a copy point, same as the value of a return statement (il-spec §10). A void body is an expression statement, so it is not copied
    // A method's expression body becomes a return statement even when void (for a void method the backend evaluates the expression, discards it, then returns). Only non-void bodies copy the value
    public bool BuildReturnInto(Node expr, List<IlStat> acc)
    {
        AddPreDecls(expr, acc);
        var sym = Ctx.CurrentFunction;
        var built = BuildExpr(expr);
        if (built == null) return false;
        bool isVoid = sym != null && sym.Type.Kind == TypeKind.Void;
        AddReturnStat(acc, isVoid ? built : WrapStructCopy(expr, built), Origin(expr));
        return true;
    }

    // Expression in statement position
    public bool BuildExprStatInto(Node expr, Node? originNode, List<IlStat> acc)
    {
        IlPos? origin = originNode != null ? Origin(originNode) : null;
        if (expr.Kind == NodeKind.PostIncDec || expr.Kind == NodeKind.PreIncDec)
            return BuildIncrementInto(expr.A!, expr.Text == "++", origin, acc);
        switch (expr.Kind)
        {
            case NodeKind.Call:
                {
                    if (expr.CondTop && expr.CondChain)
                    {
                        var condCall = BuildExpr(expr);
                        if (condCall == null) return false;
                        acc.Add(new IlCallStat(condCall, origin));
                        return true;
                    }
                    if (IsUserOutMethod(expr.Sym)) return BuildUserOutStatInto(expr, expr.Sym!, origin, acc);
                    if (BuildDictAddStatInto(expr, origin, acc)) return true;
                    if (IsRefOutCall(expr)) return BuildRefMultiReturnStatInto(expr, origin, acc);
                    var call = BuildExpr(expr);
                    if (call == null) return false;
                    acc.Add(new IlCallStat(call, origin));
                    return true;
                }
            case NodeKind.Assign:
                return BuildAssignInto(expr, origin, acc);
            // `new W();`: creates it for the constructor's side effects and discards the value
            case NodeKind.New:
                {
                    var created = BuildExpr(expr);
                    if (created == null) return false;
                    acc.Add(new IlCallStat(created, origin));
                    return true;
                }
        }
        return UnportedStat("expression statement", expr);
    }

    // Value copy points (il-spec §10): assignment / argument / return / read in a value context. A new value is not referenced from anywhere else, so it is not copied.
    // A readonly struct is immutable, so the copy is skipped. For a T? of a struct, the inner struct is copied too if it has a value
    public IlExpr WrapStructCopy(Node src, IlExpr built)
    {
        if (IsFreshStructIl(built) || IsFreshStructValue(src)) return built;
        // The natural type of the expression (before implicit conversion). An expression without a natural type (a target-typed `c ? h.F : null`) uses the conversion target type
        var t = StripConv(src).Type;
        if (t == null || t.Kind == TypeKind.Null || t.Kind == TypeKind.Error) t = src.Type;
        if (t == null) return built;
        if (IlTypes.IsNullableValueType(t) && IlTypes.IsMutableStruct(t.Elem))
            return new IlStructCopy(built, TypeName(t.Elem), true, null);
        return CopyIfMutableStruct(t, built);
    }

    // The value of a struct parameter that is written to / captured by a lambda, copied at entry (il-spec §10). null if not copied.
    // The decision stays inside the callee's body, so a body-only hot reload does not leave it stale
    public IlExpr? ParameterEntryCopy(Node fn, Node p)
    {
        if (Ctx.KeepsArgumentCopies) return null;
        // An out parameter does not receive the caller's value, so there is nothing to copy
        if (p.Has(Mods.Out) || p.Local == null) return null;
        var t = p.Local.Type;
        if (!CopiesOnEntryWhenWritten(t)) return null;
        if (!(IsCapturedByLambda(fn, p.Local) || IsWritten(fn, p.Local, false))) return null;
        return CopyIfMutableStruct(t, new IlVar(IlNames.ParamName(p), null));
    }

    // Targets of the entry copy. Generic structs are excluded because only the caller has the copier for the type argument
    private static bool CopiesOnEntryWhenWritten(TypeSym? t)
    {
        if (!IlTypes.IsMutableStruct(t)) return false;
        return t!.Kind == TypeKind.Struct && (t.Args == null || t.Args.Count == 0);
    }

    // Whether n contains an identifier referring to variable
    private static bool Mentions(Node n, LocalSym variable)
    {
        if (n.Kind == NodeKind.Name && n.Local == variable) return true;
        return AnyChild(n, c => Mentions(c, variable));
    }

    private static bool IsCapturedByLambda(Node scope, LocalSym variable)
    {
        if (scope.Kind == NodeKind.Lambda) return Mentions(scope, variable);
        return AnyChild(scope, c => IsCapturedByLambda(c, variable));
    }

    // Whether n contains an occurrence that may modify the value of variable. parentIsAssign means n is a direct child of an assignment
    private static bool IsWritten(Node n, LocalSym variable, bool parentIsAssign)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
                if (RootsAt(n.A!, variable)) return true;
                if (IsWritten(n.A!, variable, true)) return true;
                return n.B != null && IsWritten(n.B, variable, true);
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (RootsAt(n.A!, variable)) return true;
                break;
            case NodeKind.Arg:
                if ((n.Has(Mods.Ref) || n.Has(Mods.Out) || n.Has(Mods.In)) && n.A != null && RootsAt(n.A, variable)) return true;
                break;
            case NodeKind.Tuple:
            case NodeKind.DeconstructAssign:
                if (n.List != null)
                {
                    foreach (var e in n.List)
                    {
                        var x = e.Kind == NodeKind.Arg ? e.A : e;
                        if (x != null && RootsAt(x, variable)) return true;
                    }
                }
                break;
            case NodeKind.Call:
                {
                    // A call to a non-readonly instance method may modify the receiver
                    var callee = n.A;
                    if (callee != null && callee.Kind == NodeKind.Member && callee.A != null && n.Sym != null
                        && n.Info != CallKind.Extension && n.Sym.Kind == SymKind.Method && !n.Sym.IsStatic
                        && n.Sym.Builtin == "" && !(n.Sym.Decl != null && n.Sym.Decl.Has(Mods.Readonly))
                        && RootsAt(callee.A, variable))
                        return true;
                    break;
                }
            case NodeKind.Member:
                // Reading a property whose getter is not readonly may also modify the receiver (except as a direct child of an assignment)
                if (!parentIsAssign && n.A != null && n.Sym != null && n.Sym.Kind == SymKind.Property && n.Sym.Builtin == ""
                    && !n.Sym.IsStatic && !n.Sym.IsAuto && !(n.Sym.Decl != null && n.Sym.Decl.Has(Mods.Readonly))
                    && RootsAt(n.A, variable))
                    return true;
                break;
        }
        return AnyChild(n, c => IsWritten(c, variable, false));
    }

    // Whether e is a location inside the value of variable (follows parentheses and value-type fields / auto-implemented properties)
    private static bool RootsAt(Node e, LocalSym variable)
    {
        switch (e.Kind)
        {
            case NodeKind.Paren:
            case NodeKind.Conv:
                return RootsAt(e.A!, variable);
            case NodeKind.Name:
                return e.Local == variable;
            case NodeKind.Member:
                if (e.A == null || e.Sym == null) return false;
                if (e.Sym.Kind != SymKind.Field && !(e.Sym.Kind == SymKind.Property && (e.Sym.IsAuto || e.Sym.Builtin != "")))
                    return false;
                if (e.Type == null || !e.Type.IsValueType) return false;
                return RootsAt(e.A, variable);
        }
        return false;
    }

    // Skips the copy for an argument at a location that does not change during the call (a local / parameter not captured by a lambda that does not appear in later arguments or the receiver)
    private bool CanShareArgument(Symbol? callee, List<Node> args, int offset, int index, Node? receiver)
    {
        if (Ctx.KeepsArgumentCopies || callee == null || callee.Params == null) return false;
        if (index + offset >= callee.Params.Count) return false;
        var parameter = callee.Params[index + offset];
        if (parameter.IsOut || !CopiesOnEntryWhenWritten(parameter.Type)) return false;
        if (callee.Owner == null || callee.Owner.IsRef || callee.Builtin != "") return false;
        if (callee.Decl == null || !(callee.Decl.Kind == NodeKind.Method || callee.Decl.Kind == NodeKind.OperatorDecl || callee.Decl.Kind == NodeKind.Ctor)) return false;
        var arg = args[index];
        if (arg.Kind != NodeKind.Name || arg.Local == null) return false;
        var variable = arg.Local;
        if (ScopeRoots.Count == 0) return false;
        var scope = ScopeRoots[ScopeRoots.Count - 1];
        if (IsCapturedByLambda(scope, variable)) return false;
        for (int i = index + 1; i < args.Count; i++)
        {
            if (Mentions(args[i], variable)) return false;
        }
        if (receiver != null && Mentions(receiver, variable)) return false;
        return true;
    }

    // The expression of a call argument (the contents of Arg)
    private static List<Node> ArgExprs(Node call)
    {
        var r = new List<Node>();
        if (call.List == null) return r;
        foreach (var a in call.List) r.Add(a.A!);
        return r;
    }

    // Gives it its type as an IL expression (the C backend requires both sides of a ternary and ?? to have the same type)
    private static IlExpr Ex(IlExpr e)
    {
        return e;
    }

    // ---- Expressions ----

    public IlExpr? BuildExpr(Node expr)
    {
        if (_subNode != null && expr == _subNode) return _subIl;
        if (expr.CondTop && expr.CondChain && expr != _condBuilding) return BuildConditionalTop(expr);
        switch (expr.Kind)
        {
            case NodeKind.Conv:
                return BuildConversion(expr);
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.BoolLit:
            case NodeKind.NullLit:
                return BuildLiteral(expr);
            case NodeKind.Paren:
                {
                    var inner = BuildExpr(expr.A!);
                    return inner == null ? null : new IlParen(inner, null);
                }
            // checked / unchecked set the context of the inner operations (CheckedIntCall)
            case NodeKind.Checked:
                {
                    bool outer = _checked;
                    _checked = expr.Value == 1;
                    var inner = BuildExpr(expr.A!);
                    _checked = outer;
                    return inner == null ? null : new IlParen(inner, null);
                }
            case NodeKind.This:
                return new IlVar("self", null);
            case NodeKind.Name:
                return BuildIdentifier(expr);
            case NodeKind.InterpString:
                return BuildInterpolatedString(expr);
            case NodeKind.Binary:
                return BuildBinary(expr);
            case NodeKind.Call:
                return BuildInvocation(expr);
            case NodeKind.Member:
                return BuildMemberAccess(expr);
            case NodeKind.New:
                return BuildObjectCreation(expr);
            case NodeKind.Unary:
                return BuildPrefixUnary(expr);
            case NodeKind.Assign:
                return BuildAssignExpr(expr);
            case NodeKind.Tuple:
                return BuildTupleLiteral(expr);
            case NodeKind.PreIncDec:
                return BuildIncrementExpr(expr.A!, expr.Text == "++", true);
            case NodeKind.PostIncDec:
                return BuildIncrementExpr(expr.A!, expr.Text == "++", false);
            case NodeKind.Cast:
                return BuildCast(expr);
            case NodeKind.Conditional:
                {
                    var cond = BuildExpr(expr.A!);
                    var t = BuildExpr(expr.B!);
                    var f = BuildExpr(expr.C!);
                    if (cond == null || t == null || f == null) return null;
                    return new IlTernary(cond, t, f, null);
                }
            case NodeKind.Index:
                return BuildElementAccess(expr);
            case NodeKind.DefaultLit:
                return DefaultIl(expr.Type);
            case NodeKind.Is:
                return BuildIsPattern(expr);
            case NodeKind.As:
                return BuildAsExpr(expr);
            case NodeKind.SwitchExpr:
                return BuildSwitchExpression(expr);
            case NodeKind.Lambda:
                return BuildLambda(expr);
            case NodeKind.NewArray:
                return BuildNewArray(expr);
            case NodeKind.ArrayInit:
                {
                    var at = expr.Type;
                    return BuildArrayItems(expr, at != null && at.Kind == TypeKind.Array ? IlTypes.Display(at.Elem) : null);
                }
        }
        return Unported("expression", expr);
    }

    // Implicit and explicit conversions (the Binder's Conv). Numeric conversions and nullable conversions
    private IlExpr? BuildConversion(Node conv)
    {
        bool promoted = conv == _promotedOperand;
        _promotedOperand = null;
        // A numeric conversion applies to the expression inside the parentheses (Roslyn also attaches the target type to the expression inside the parentheses)
        if ((conv.Info == ConvKind.IntToFloat || conv.Info == ConvKind.NullableIntToFloat) && conv.A!.Kind == NodeKind.Paren && !promoted)
        {
            var innerConv = new Node(NodeKind.Conv, conv.File, conv.Pos);
            innerConv.End = conv.End;
            innerConv.A = conv.A.A;
            innerConv.Type = conv.Type;
            innerConv.Info = conv.Info;
            innerConv.HasConst = conv.HasConst;
            innerConv.Const = conv.Const;
            var converted = BuildConversion(innerConv);
            return converted == null ? null : new IlParen(converted, null);
        }
        var inner = BuildExpr(conv.A!);
        if (inner == null) return null;
        switch (conv.Info)
        {
            case ConvKind.Identity:
                // Conversion to float of an integer constant folded by the binder (becomes a float literal; arithmetic operands are excluded)
                if (conv.HasConst && conv.Const != null && conv.Const.Kind == ConstKind.Float && !promoted
                    && IlTypes.UnwrapNullable(conv.Type!).Kind == TypeKind.Float && IlTypes.IsIntegralSource(IlTypes.UnwrapNullable(NaturalType(conv.A!)))
                    && !IsFloatConverted(inner))
                    inner = FloatLit(conv.Const.F);
                return ApplyNullableConversion(conv, inner);
            case ConvKind.MethodGroup:
                return inner;
            case ConvKind.NullableIntToFloat:
                {
                    // int? → float? converts only when there is a value
                    if (IlTypes.UnwrapNullable(conv.Type!).Kind != TypeKind.Float) return inner;
                    if (IsFloatConverted(inner) || promoted) return inner;
                    if (!IlTypes.IsNullableValueType(NaturalType(conv.A!))) return ApplyNullableConversion(conv, new IlNumericConvert(inner, "float", false, null));
                    return new IlLiftedUn(IlUnOp.ToFloat, inner, null);
                }
            case ConvKind.IntToFloat:
                {
                    // Make it an explicit conversion only when the target is float (not double)
                    if (IlTypes.UnwrapNullable(conv.Type!).Kind != TypeKind.Float) return ApplyNullableConversion(conv, inner);
                    if (IsFloatConverted(inner) || promoted) return ApplyNullableConversion(conv, inner);
                    if (conv.A!.HasConst && conv.A.Const != null)
                    {
                        var c = conv.A.Const;
                        float f = c.Kind == ConstKind.Float ? c.F : (float)c.I;
                        return ApplyNullableConversion(conv, FloatLit(f));
                    }
                    // int → float? wraps into T? after the numeric conversion
                    return ApplyNullableConversion(conv, new IlNumericConvert(inner, "float", false, null));
                }
        }
        return Unported("conversion " + conv.Info, conv);
    }

    // Operands of built-in float arithmetic (+ - * / %) skip the explicit conversion because the VM and C operations promote int to float (BuildBinary marks them)
    private bool IsPromotedArithmeticOperand(Node conv)
    {
        return conv == _promotedOperand;
    }

    private Node? _promotedOperand;
    // Whether inside a checked context (i32 + - * and unary - become helpers that fault on overflow)
    private bool _checked;

    private static bool IsFloatConverted(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        if (built is IlNumericConvert nc && nc.TargetType == "float") return true;
        if (built is IlLit lit && lit.Kind == IlLitKind.Float) return true;
        if (built is IlLiftedUn lu && lu.Op == IlUnOp.ToFloat) return true;
        return false;
    }

    // f32 literal
    public static IlExpr FloatLit(float value) => IlLiterals.Float(value);

    private IlExpr? BuildLiteral(Node lit)
    {
        switch (lit.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
                if (lit.Const == null) return Unported("literal without a value", lit);
                return lit.Kind == NodeKind.FloatLit ? IlLiterals.Float(lit.Const.F) : IlLiterals.Int(lit.Const.I);
            case NodeKind.StringLit:
                return IlLiterals.Str(lit.Text);
            case NodeKind.CharLit:
                return IlLiterals.Int(lit.Value);
            case NodeKind.BoolLit:
                return IlLiterals.Bool(lit.Value != 0);
            case NodeKind.NullLit:
                return IlLiterals.Null();
        }
        return Unported("literal", lit);
    }

    // Interpolated string (alignment only as a literal)
    private IlExpr? BuildInterpolatedString(Node interp)
    {
        var parts = new List<IlExpr>();
        if (interp.List != null)
        {
            foreach (var content in interp.List)
            {
                if (content.Kind == NodeKind.StringLit)
                {
                    parts.Add(IlLiterals.Str(content.Text));
                    continue;
                }
                var inner = BuildExpr(content.A!);
                if (inner == null) return null;
                // bool / string / char do not use the format. Rune and types that override ToString also ignore the format
                var holeType = NaturalType(content.A!);
                var ht = IlTypes.UnwrapNullable(holeType);
                string? format = content.Text != "" ? content.Text : null;
                if (IlTypes.IsRune(ht) || HasToStringOverride(ht) || ht.Kind == TypeKind.Bool || ht.Kind == TypeKind.String || ht.Kind == TypeKind.Char) format = null;
                IlExpr? rendered = null;
                if (format != null)
                {
                    var enumName = WrapEnumFormat(holeType, inner, format);
                    if (enumName is IlCall ec && ec.Callee == "String.FormatEnum") rendered = enumName;
                    if (rendered == null) rendered = WrapNumberFormat(holeType, inner, format);
                    if (rendered == null) rendered = new IlCall("__tcs_format", Two(IlLiterals.Str(ConvertFormatSpecifier(format)), inner), null);
                }
                else rendered = WrapFloatToString(content.A!, inner);
                if (rendered == null) return null;
                if (content.B != null)
                {
                    if (!content.B.HasConst || content.B.Const == null || content.B.Const.Kind != ConstKind.Int) return null;
                    rendered = new IlCall("__tcs_format", Two(IlLiterals.Str("%" + content.B.Const.I + "s"), rendered), null);
                }
                parts.Add(rendered);
            }
        }
        if (parts.Count == 0) return IlLiterals.Str("");
        var result = parts[0];
        for (int i = 1; i < parts.Count; i++) result = new IlBin(IlBinOp.Concat, result, parts[i], null);
        // A `..` concatenation is not atomic (IlLen / IlField bind only to the first element), so it is wrapped in parentheses to be safe in receiver position too
        return parts.Count == 1 ? result : Ex(new IlParen(result, null));
    }

    // Standard formats of numbers (and T?) produce the same string as .NET's ToString(format) (String.FormatFloat / String.FormatInt, il-spec §13).
    // The precision is a number of up to 9 digits, and "G03" is the same as "G3", so it is passed as a number. R is the same as G, and float does not use the precision
    private static IlExpr? WrapNumberFormat(TypeSym? type, IlExpr value, string format)
    {
        bool isFloat = IlTypes.IsFloating(type);
        var u = new TinySystem.Text.Utf8Bytes(format);
        int n = u.Length;
        if ((!isFloat && !IlTypes.IsIntegral(type)) || n == 0 || n > 10) return null;
        for (int i = 1; i < n; i++)
        {
            if (u[i] < '0' || u[i] > '9') return null;
        }
        int first = u[0];
        int kind = first == 'R' ? 'G' : first == 'r' ? 'g' : first;
        var kinds = isFloat ? "GgEeFfNn" : "GgEeFfNnDdXx";
        var ku = new TinySystem.Text.Utf8Bytes(kinds);
        bool known = false;
        for (int i = 0; i < ku.Length; i++)
        {
            if (ku[i] == kind) known = true;
        }
        if (!known) return null;
        var kindText = Strings.Prefix(kind == first ? format : (kind == 'G' ? "G" : "g"), 1);
        string spec;
        if (n == 1 || (isFloat && kind != first)) spec = kindText;
        else
        {
            int digits = 0;
            for (int i = 1; i < n; i++) digits = digits * 10 + (u[i] - '0');
            spec = kindText + digits;
        }
        return new IlCall(isFloat ? "String.FormatFloat" : "String.FormatInt", Two(value, IlLiterals.Str(spec)), null);
    }

    private static bool AllZeros(string s)
    {
        var u = new TinySystem.Text.Utf8Bytes(s);
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] != '0') return false;
        }
        return true;
    }

    // Converts a C# format specifier to a string.format specifier (printf style, read by runtime/fmt)
    private static string ConvertFormatSpecifier(string fmt)
    {
        if (fmt == "") return "%s";
        var first = Strings.Prefix(fmt, 1);
        var c = first.ToUpper();
        var precision = Strings.From(fmt, 1);
        switch (c)
        {
            case "F": return precision == "" ? "%.6f" : "%." + precision + "f";
            case "N": return precision == "" ? "%.2f" : "%." + precision + "f";
            // The precision of D is the minimum number of digits excluding the sign (same as printf's %.Nd). Precision 0 is the same as no precision
            case "D": return AllZeros(precision) ? "%d" : "%." + precision + "d";
            case "X":
                if (first == "X") return precision == "" ? "%X" : "%0" + precision + "X";
                return precision == "" ? "%x" : "%0" + precision + "x";
            case "E":
                if (first == "E") return precision == "" ? "%E" : "%." + precision + "E";
                return precision == "" ? "%e" : "%." + precision + "e";
            case "G": return precision == "" ? "%g" : "%." + precision + "g";
        }
        return "%s";
    }

    // ---- Calls ----

    // ++ / -- in expression position. An IIFE that assigns once, with the same place resolution as in statement position (receivers / indices with side effects go to a temp),
    // and returns the updated value for prefix and the old value for postfix
    private IlExpr? BuildIncrementExpr(Node operand, bool increment, bool prefix)
    {
        var stats = new List<IlStat>();
        IlExpr read;
        var prop = BuildPropTarget(operand, true);
        if (prop != null)
        {
            foreach (var st in prop.Setup) stats.Add(st);
            IlExpr pv = new IlVar("__tcs_v", null);
            stats.Add(new IlLocal("__tcs_v", prefix ? StepValue(operand, BuildPropGet(prop), increment) : BuildPropGet(prop), null, null));
            stats.Add(new IlCallStat(BuildPropSet(prop, prefix ? pv : StepValue(operand, pv, increment)), null));
            stats.Add(new IlReturn(pv, null));
            return new IlIife(stats, null);
        }
        if (NeedsLoweredLvalue(operand))
        {
            var lowered = BuildLoweredTarget(operand, false);
            if (lowered == null) return null;
            foreach (var st in lowered.Setup) stats.Add(st);
            read = lowered.Access;
        }
        else
        {
            var target = BuildExpr(operand);
            if (target == null) return null;
            read = target;
        }
        IlExpr value = new IlVar("__tcs_v", null);
        stats.Add(new IlLocal("__tcs_v", prefix ? StepValue(operand, read, increment) : read, null, null));
        stats.Add(new IlAssign(read, prefix ? value : StepValue(operand, value, increment), null));
        stats.Add(new IlReturn(value, null));
        return new IlIife(stats, null);
    }

    // ---- ?. (il-spec §3) ----
    // In the self-hosted tree, ?. has CondChain on each node of the chain, CondTop on the outermost node, and Value = 1 at the `?.` / `?[` positions.
    // Builds it into the shape of Roslyn's nested ConditionalAccess (Expression = the receiver of the first ?., WhenNotNull = the chain above it; the next ?. in the chain is nested)

    private Node? _subNode;
    private IlExpr? _subIl;
    private Node? _condBuilding;
    private Node? _condBinding;

    private static bool IsCondPoint(Node n)
    {
        return (n.Kind == NodeKind.Member || n.Kind == NodeKind.Index || n.Kind == NodeKind.Call) && n.Value == 1;
    }

    private static Node? ChainNext(Node n)
    {
        if (n.Kind == NodeKind.Member || n.Kind == NodeKind.Call || n.Kind == NodeKind.Index || n.Kind == NodeKind.Conv) return n.A;
        return null;
    }

    // The nodes of a ?. chain from outside to inside (the last one is the first ?.)
    private static List<Node> ChainPath(Node top)
    {
        var path = new List<Node>();
        var n = top;
        while (true)
        {
            path.Add(n);
            if (IsCondPoint(n) && (n.A == null || !StripConv(n.A).CondChain)) break;
            var next = ChainNext(n);
            if (next == null || !(next.CondChain || (next.Kind == NodeKind.Conv && StripConv(next).CondChain))) break;
            n = next;
        }
        return path;
    }

    private IlExpr? BuildConditionalTop(Node top)
    {
        var path = ChainPath(top);
        var first = path[path.Count - 1];
        if (!IsCondPoint(first) || first.A == null) return Unported("conditional chain", top);
        // Inside the chain, build with the types before lifting (the types inside Roslyn's WhenNotNull)
        var saved = new List<TypeSym?>();
        foreach (var n in path)
        {
            saved.Add(n.Type);
            if (n.CondChain && n.Type2 != null) n.Type = n.Type2;
        }
        var outerBuilding = _condBuilding;
        _condBuilding = top;
        IlExpr? result = null;
        var receiver = BuildExpr(first.A);
        if (receiver != null) result = BuildConditionalCore(receiver, NaturalType(first.A), path, path.Count - 1, 0);
        _condBuilding = outerBuilding;
        for (int i = 0; i < path.Count; i++) path[i].Type = saved[i];
        return result;
    }

    // The IIFE built by BuildConditionalCore (`__tcs_ca = ...; if (__tcs_ca != nil) ...`)
    private static bool IsConditionalCore(IlExpr e)
    {
        if (!(e is IlIife iife) || iife.Stats.Count == 0) return false;
        return iife.Stats[0] is IlLocal l && l.Name == "__tcs_ca";
    }

    // An IIFE that evaluates receiver once into a local and, if it is not null, returns path[bottom..top] (path[bottom] is the ?. position).
    // For a `T?` receiver, the has-value check and the inner value are explicit nodes. If the result is a value type (not T?), it is wrapped in T?
    private IlExpr? BuildConditionalCore(IlExpr receiver, TypeSym receiverType, List<Node> path, int bottom, int top)
    {
        bool nullableValue = IlTypes.IsNullableValueType(receiverType);
        var underlying = nullableValue ? receiverType.Elem : receiverType;
        var objName = nullableValue ? "__tcs_cav" : "__tcs_ca";
        var whenNotNull = BuildConditionalWhenNotNull(path, bottom, top, new IlVar(objName, null), underlying);
        if (whenNotNull == null) return null;
        var resultType = path[top].Type;
        // The result of an inner ?. (a nested IIFE) is already T? (the type of Roslyn's nested ConditionalAccess)
        if (resultType != null && resultType.IsValueType && resultType.Kind != TypeKind.Void && !IlTypes.IsNullableValueType(resultType) && !(whenNotNull is IlNullableWrap)
            && !IsConditionalCore(whenNotNull))
            whenNotNull = new IlNullableWrap(whenNotNull, IlTypes.Display(resultType), null);
        var ca = new IlVar("__tcs_ca", null);
        IlExpr cond = nullableValue ? new IlNullableHasValue(ca, null) : new IlBin(IlBinOp.Ne, ca, IlLiterals.Null(), null);
        var thenStats = new List<IlStat>();
        if (nullableValue) thenStats.Add(new IlLocal(objName, new IlNullableValue(ca, null), null, null));
        thenStats.Add(new IlReturn(whenNotNull, null));
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ca", receiver, null, null));
        stats.Add(new IlIf(Arm(cond, new IlBlock(thenStats)), null, null));
        return new IlIife(stats, null);
    }

    private IlExpr? BuildConditionalWhenNotNull(List<Node> path, int bottom, int top, IlExpr obj, TypeSym receiverType)
    {
        // The next ?. in the chain (a nested ConditionalAccess)
        for (int i = bottom - 1; i >= top; i--)
        {
            if (!IsCondPoint(path[i])) continue;
            // The nested Expression is path[i].A (= path[i + 1] and below)
            var inner = BuildConditionalWhenNotNull(path, bottom, i + 1, obj, receiverType);
            if (inner == null) return null;
            return BuildConditionalCore(inner, NaturalType(path[i].A!), path, i, top);
        }
        // The binding of the first ?. (for a method, the binding extends to the call)
        var b = path[bottom];
        int rootIndex = bottom;
        if (b.Kind == NodeKind.Member && bottom - 1 >= top && path[bottom - 1].Kind == NodeKind.Call && path[bottom - 1].A == b) rootIndex = bottom - 1;
        var rootIl = BuildConditionalBinding(path[rootIndex], b, obj, receiverType);
        if (rootIl == null || rootIndex == top) return rootIl;
        // The rest of the chain: build it with the binding replaced by the built IL
        var outerSub = _subNode;
        var outerSubIl = _subIl;
        _subNode = path[rootIndex];
        _subIl = rootIl;
        var outerBuilding = _condBuilding;
        _condBuilding = path[top];
        var built = BuildExpr(path[top]);
        _condBuilding = outerBuilding;
        _subNode = outerSub;
        _subIl = outerSubIl;
        return built;
    }

    // Builds the binding at the ?. position (`.M` / `.M(args)` / `[i]` / `d?.Invoke(args)`) with obj as the receiver
    private IlExpr? BuildConditionalBinding(Node root, Node binding, IlExpr obj, TypeSym receiverType)
    {
        var recvNode = binding.A!;
        // A custom property of a struct is read on a copy of the receiver (the .Value of `S?` is a copy)
        if (root == binding && binding.Kind == NodeKind.Member && binding.Sym != null && binding.Sym.Kind == SymKind.Property && IsCustomProperty(binding.Sym) && IlTypes.IsUserStruct(receiverType))
            return new IlCall(TypeRef(binding.Sym.Owner!) + ".get_" + N(binding.Sym), One(ConditionalStructReceiver(obj, receiverType)), null);
        var savedType = recvNode.Type;
        recvNode.Type = receiverType;
        var outerSub = _subNode;
        var outerSubIl = _subIl;
        var outerBinding = _condBinding;
        _subNode = recvNode;
        _subIl = obj;
        _condBinding = binding;
        var outerBuilding = _condBuilding;
        _condBuilding = root;
        var built = BuildExpr(root);
        _condBuilding = outerBuilding;
        _condBinding = outerBinding;
        _subNode = outerSub;
        _subIl = outerSubIl;
        recvNode.Type = savedType;
        return built;
    }

    private IlExpr ConditionalStructReceiver(IlExpr obj, TypeSym type)
    {
        if (!IlTypes.IsMutableStruct(type)) return obj;
        return new IlStructCopy(obj, TypeName(type), false, null);
    }

    // ---- Tuples and deconstruction. A tuple value is a struct (TinySystem/ValueTuple.cs)

    private int _deconstructTemp;

    private static Node WithoutParens(Node e)
    {
        while (e.Kind == NodeKind.Paren) e = e.A!;
        return e;
    }

    private static Node TupleElementNode(Node e)
    {
        return e.Kind == NodeKind.Arg ? e.A! : e;
    }

    private IlExpr? BuildTupleLiteral(Node tuple)
    {
        var entries = new List<IlTableEntry>();
        if (tuple.List != null)
        {
            for (int i = 0; i < tuple.List.Count; i++)
            {
                var el = TupleElementNode(tuple.List[i]);
                var value = BuildExpr(el);
                if (value == null) return null;
                entries.Add(new IlTableEntry(null, WrapStructCopy(el, value), "Item" + (i + 1)));
            }
        }
        return new IlTable(entries, null, null, false, null, null);
    }

    // ---- Deconstruction. C# order: left-side receivers and indices → right side → assignments from left to right ----

    private static DTarget? DeconstructTargetOf(Node target)
    {
        var t = StripConv(target);
        switch (t.Kind)
        {
            case NodeKind.PatDecl:
                if (t.Local == null) return null;
                return DTarget.Declared(t.Local.IlName, t.Local.Type);
            case NodeKind.PatDiscard:
                return DTarget.Discard();
            case NodeKind.DeconstructAssign:
            case NodeKind.Tuple:
                {
                    var items = new List<DTarget>();
                    if (t.List != null)
                    {
                        foreach (var x in t.List)
                        {
                            var item = DeconstructTargetOf(TupleElementNode(x));
                            if (item == null) return null;
                            items.Add(item);
                        }
                    }
                    return DTarget.Nested(items);
                }
            case NodeKind.Name:
                if (t.Text == "_" && t.Info != RefKind.Local && t.Sym == null) return DTarget.Discard();
                return t.Type != null ? DTarget.PlaceOf(t, t.Type) : null;
            case NodeKind.Member:
            case NodeKind.Index:
                return t.Type != null ? DTarget.PlaceOf(t, t.Type) : null;
        }
        return null;
    }

    // Components of deconstructible types: tuples, records (positional properties)
    private static List<ValueMember>? DeconstructMembers(TypeSym? type, int count)
    {
        if (type == null) return null;
        type = Binder.TupleViewOf(type);
        if (type.Kind == TypeKind.Tuple)
        {
            var r = new List<ValueMember>();
            for (int i = 0; i < type.Args!.Count; i++) r.Add(new ValueMember("Item" + (i + 1), type.Args[i]));
            return r;
        }
        if (type.Decl == null) return null;
        var props = new List<ValueMember>();
        foreach (var m in type.Decl.Members)
        {
            if (m.Kind != SymKind.Property || m.IsStatic) continue;
            if (m.Decl != null && m.Decl.Kind == NodeKind.Property && !m.Decl.Has(Mods.Public)) continue;
            props.Add(new ValueMember(m.Name, type.Args != null && type.Args.Count > 0 ? m.Type.Subst(type.Args, null) : m.Type));
        }
        if (props.Count < count) return null;
        var taken = new List<ValueMember>();
        for (int i = 0; i < count; i++) taken.Add(props[i]);
        return taken;
    }

    private bool BuildDeconstructInto(Node targets, Node right, IlPos? origin, List<IlStat> acc)
    {
        var target = DeconstructTargetOf(targets);
        if (target == null || target.Kind != DTarget.KNested) return false;
        var rhs = WithoutParens(right);
        int flat = TryBuildFlatDeconstruct(target, rhs, origin, acc);
        if (flat >= 0) return flat == 1;
        var stats = new List<IlStat>();
        var assigns = new List<DAssign>();
        var declared = new List<DTarget>();
        DeclaredNames(target, declared);
        foreach (var d in declared) acc.Add(new IlLocal(d.Name, null, IlTypes.Display(d.Type!), origin));
        if (!FixPlaces(target, stats)) return false;
        if (!BindValues(target, rhs, stats, assigns)) return false;
        AddAssigns(assigns, stats);
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    private static void DeclaredNames(DTarget t, List<DTarget> into)
    {
        if (t.Kind == DTarget.KDeclared) into.Add(t);
        else if (t.Kind == DTarget.KNested)
        {
            foreach (var i in t.Items) DeclaredNames(i, into);
        }
    }

    private string NewDeconstructTemp()
    {
        _deconstructTemp++;
        return "__tcs_d" + _deconstructTemp;
    }

    // A declaration-only deconstruction (`var (a, b) = ...`) and an assignment to locals can bind the right-side values at once. 1 = built, 0 = failed, -1 = not applicable
    private int TryBuildFlatDeconstruct(DTarget target, Node rhs, IlPos? origin, List<IlStat> acc)
    {
        bool declare = true;
        bool locals = true;
        foreach (var i in target.Items)
        {
            if (i.Kind != DTarget.KDeclared && i.Kind != DTarget.KDiscard) declare = false;
            if (!(i.Kind == DTarget.KPlace && i.Place!.Kind == NodeKind.Name && i.Place.Local != null)) locals = false;
        }
        if (!declare && !locals) return -1;
        var names = new List<string>();
        foreach (var i in target.Items)
        {
            if (i.Kind == DTarget.KDeclared) names.Add(i.Name);
            else if (i.Kind == DTarget.KPlace) names.Add(i.Place!.Local!.IlName);
            else names.Add("_");
        }
        if (!declare)
        {
            var seen = new HashSet<string>();
            foreach (var n in names)
            {
                if (!seen.Add(n)) return -1;
            }
        }
        var targets = new List<IlExpr>();
        foreach (var n in names) targets.Add(new IlVar(n, null));
        if (rhs.Kind == NodeKind.Tuple && rhs.List != null && rhs.List.Count == targets.Count)
        {
            foreach (var a in rhs.List)
            {
                if (WithoutParens(StripConv(TupleElementNode(a))).Kind == NodeKind.Tuple) return -1;
            }
            var values = new List<IlExpr>();
            for (int i = 0; i < targets.Count; i++)
            {
                var el = TupleElementNode(rhs.List[i]);
                var built = BuildExpr(el);
                if (built == null) return 0;
                var tt = target.Items[i].Type;
                var src = el.Type;
                if (FoldsToFloatConst(el, tt))
                {
                    built = FloatLit((float)el.Const!.I);
                    src = TypeSym.FloatType;
                }
                values.Add(ConvertDeconstructed(WrapStructCopy(el, built), src, tt));
            }
            acc.Add(new IlMultiAssign(targets, values, declare, origin));
            return 1;
        }
        return -1;
    }

    // Fixes the receivers and indices of the places the elements are assigned to before evaluating the right side
    private bool FixPlaces(DTarget target, List<IlStat> stats)
    {
        if (target.Kind == DTarget.KNested)
        {
            foreach (var i in target.Items)
            {
                if (!FixPlaces(i, stats)) return false;
            }
            return true;
        }
        if (target.Kind != DTarget.KPlace) return true;
        var place = target.Place!;
        if (IsCustomPropertyTarget(place)) return false;
        bool direct = place.Kind == NodeKind.Name;
        if (!direct && place.Kind == NodeKind.Member && place.A != null && NaturalType(place.A).IsValueType)
        {
            var baseExpr = StructPlaceBase(place.A);
            direct = baseExpr == null || baseExpr == place.A;
        }
        if (direct)
        {
            var built = BuildExpr(place);
            if (built == null) return false;
            target.Fixed = built;
            return true;
        }
        var lowered = BuildLoweredTarget(place, true);
        if (lowered == null) return false;
        target.Fixed = RenameFixedTemps(lowered.Setup, lowered.Access, stats);
        return true;
    }

    // BuildLoweredTarget's temp has a fixed name, so each place gets a different name
    private IlExpr RenameFixedTemps(List<IlStat> setup, IlExpr access, List<IlStat> into)
    {
        var renames = new Dictionary<string, string>();
        foreach (var stat in setup)
        {
            var local = (IlLocal)stat;
            var init = RenameVars(local.Init!, renames);
            renames[local.Name] = NewDeconstructTemp();
            into.Add(local with { Name = renames[local.Name], Init = init });
        }
        return RenameVars(access, renames);
    }

    private static IlExpr RenameVars(IlExpr e, Dictionary<string, string> renames)
    {
        if (e is IlVar v && renames.TryGetValue(v.Name, out var name)) return new IlVar(name, null);
        var kids = IlTree.Children(e);
        if (kids.Count == 0) return e;
        var renamed = new List<IlExpr>();
        foreach (var k in kids) renamed.Add(RenameVars(k, renames));
        return IlTree.WithChildren(e, renamed);
    }

    private bool BindValues(DTarget target, Node rhs, List<IlStat> stats, List<DAssign> assigns)
    {
        rhs = WithoutParens(rhs);
        if (target.Kind == DTarget.KNested)
        {
            if (rhs.Kind == NodeKind.Tuple)
            {
                if (rhs.List == null || rhs.List.Count != target.Items.Count) return false;
                for (int i = 0; i < target.Items.Count; i++)
                {
                    if (!BindValues(target.Items[i], TupleElementNode(rhs.List[i]), stats, assigns)) return false;
                }
                return true;
            }
            var type = NaturalType(rhs);
            var built = BuildExpr(rhs);
            if (built == null) return false;
            return BindValue(target, built, type, stats, assigns);
        }
        var value = BuildExpr(rhs);
        if (value == null) return false;
        value = WrapStructCopy(rhs, value);
        var sourceType = rhs.Type;
        if (FoldsToFloatConst(rhs, target.Type))
        {
            value = FloatLit((float)rhs.Const!.I);
            sourceType = TypeSym.FloatType;
        }
        var temp = NewDeconstructTemp();
        var declType = target.Type ?? sourceType;
        stats.Add(new IlLocal(temp, value, declType != null ? IlTypes.Display(declType) : null, null));
        assigns.Add(new DAssign(target, ConvertDeconstructed(new IlVar(temp, null), sourceType, target.Type)));
        return true;
    }

    // Binds the value to a temp and then passes it to target. Nested ones are followed into their components
    private bool BindValue(DTarget target, IlExpr value, TypeSym? type, List<IlStat> stats, List<DAssign> assigns)
    {
        if (target.Kind != DTarget.KNested)
        {
            assigns.Add(new DAssign(target, ConvertDeconstructed(CopyIfMutableStruct(type, value), type, target.Type)));
            return true;
        }
        var members = DeconstructMembers(type, target.Items.Count);
        if (members == null || members.Count != target.Items.Count) return false;
        var holder = value;
        // A value that is not a deconstruction temp (including user variables) is put in a temp. Even if an assignment target is a component of a right-side variable, the right side reads the value before the assignment
        if (!(value is IlVar v && v.Name.StartsWith("__tcs_d")))
        {
            var temp = NewDeconstructTemp();
            stats.Add(new IlLocal(temp, (value is IlVar) ? CopyIfMutableStruct(type, value) : value, type != null ? IlTypes.Display(type) : null, null));
            holder = new IlVar(temp, null);
        }
        for (int i = 0; i < members.Count; i++)
        {
            if (!BindValue(target.Items[i], new IlField(holder, members[i].Name, null), members[i].Type, stats, assigns)) return false;
        }
        return true;
    }

    // Integer constant elements of a tuple literal into float deconstruction targets: Roslyn converts literal elements to the target type (the binder does not look at the target type of the expression, so it has no conversion)
    private static bool FoldsToFloatConst(Node el, TypeSym? target)
    {
        if (target == null || IlTypes.UnwrapNullable(target).Kind != TypeKind.Float) return false;
        return el.Kind != NodeKind.Conv && el.HasConst && el.Const != null && el.Const.Kind == ConstKind.Int && IlTypes.IsIntegralSource(el.Type);
    }

    // Implicit conversion of deconstruction components (`float f; (f, g) = (1, 2)`)
    private static IlExpr ConvertDeconstructed(IlExpr value, TypeSym? source, TypeSym? target)
    {
        if (target == null || source == null || TypeSym.Same(source, target)) return value;
        var inner = IlTypes.IsNullableValueType(target) ? target.Elem : target;
        if (inner.Kind == TypeKind.Float && IlTypes.IsIntegralSource(source)) value = new IlNumericConvert(value, "float", false, null);
        if (IlTypes.IsNullableValueType(target) && !IlTypes.IsNullableValueType(source)) return new IlNullableWrap(value, IlTypes.Display(inner), null);
        return value;
    }

    private static void AddAssigns(List<DAssign> assigns, List<IlStat> stats)
    {
        foreach (var a in assigns)
        {
            if (a.Target.Kind == DTarget.KDeclared) stats.Add(new IlAssign(new IlVar(a.Target.Name, null), a.Value, null));
            else if (a.Target.Kind == DTarget.KPlace) stats.Add(new IlAssign(a.Target.Fixed!, a.Value, null));
        }
    }

    // `foreach (var (a, b) in list)`: receives the element in one variable and deconstructs it at the start of the body
    private bool BuildForeachDeconstructInto(Node loop, List<IlStat> acc)
    {
        var collType = NaturalType(loop.B!);
        TypeSym? elementType = null;
        if (collType.Kind == TypeKind.Array || collType.Kind == TypeKind.List) elementType = collType.Elem;
        if (elementType == null) return false;
        var items = new List<DTarget>();
        foreach (var nm in loop.List!)
        {
            if (nm.Text == "_" && nm.Local == null) items.Add(DTarget.Discard());
            else if (nm.Local != null) items.Add(DTarget.Declared(nm.Local.IlName, nm.Local.Type));
            else return false;
        }
        var target = DTarget.Nested(items);
        var collection = BuildExpr(loop.B!);
        var body = BuildBlock(loop.C!);
        if (collection == null || body == null) return false;
        var stats = new List<IlStat>();
        var assigns = new List<DAssign>();
        var declared = new List<DTarget>();
        DeclaredNames(target, declared);
        var all = new List<IlStat>();
        foreach (var d in declared) all.Add(new IlLocal(d.Name, null, IlTypes.Display(d.Type!), null));
        var item = NewDeconstructTemp();
        if (!BindValue(target, new IlVar(item, null), elementType, stats, assigns)) return false;
        AddAssigns(assigns, stats);
        foreach (var s in stats) all.Add(s);
        foreach (var s in body.Stats) all.Add(s);
        acc.Add(new IlForeachList(item, collection, new IlBlock(all), Origin(loop)));
        return true;
    }

    // ---- User operators ----

    // A user operator directly calls the static function of the overload chosen at the call site (`V.__mul_2(a, b)`).
    // Operators of --ref types are implemented in the host, so they do not become calls (they stay IL operations). Struct arguments are copied and passed, as in calls
    private IlExpr? TryBuildUserOperatorCall(Node expr, List<Node> operands, List<IlExpr> built)
    {
        var op = expr.Sym;
        if (op == null || op.Kind != SymKind.Method || op.Builtin != "" || op.Owner == null || op.Owner.IsInterface || op.Owner.IsRef || op.Decl == null) return null;
        var name = IlNames.OperatorName(op.Decl);
        if (name == null) return null;
        var args = new List<IlExpr>();
        for (int i = 0; i < built.Count; i++)
        {
            if (CanShareArgument(op, operands, 0, i, null)) args.Add(built[i]);
            else args.Add(WrapStructCopy(operands[i], built[i]));
        }
        foreach (var a in OperatorTypeArgDefaults(op, operands)) args.Add(a);
        return new IlCall(TypeRef(op.Owner) + "." + name, args, null);
    }

    // Operators of generic types take the default values of the type arguments at the end, like static methods. The type arguments come from the operand of its own type
    private List<IlExpr> OperatorTypeArgDefaults(Symbol op, List<Node> operands)
    {
        var r = new List<IlExpr>();
        if (!IlTypes.PassesTypeArgDefaults(op.Owner)) return r;
        List<TypeSym>? args = null;
        foreach (var o in operands)
        {
            var t = IlTypes.UnwrapNullable(NaturalType(o));
            if (args == null && t.Decl == op.Owner && t.Args != null) args = t.Args;
        }
        if (args == null) args = op.Owner!.SelfType().Args;
        foreach (var a in args!) r.Add(TypeArgDefaultValue(a));
        return r;
    }

    // ---- out arguments ----

    // out of user-defined methods: the call is an IlMultiAssign (the VM returns the out values after the return value, and C copies them through pointers). The targets are in parameter order, with the return value first
    private static bool IsUserOutMethod(Symbol? m)
    {
        return m != null && m.Kind == SymKind.Method && m.HasOut && m.Builtin == "" && m.Owner != null && !m.Owner.IsRef;
    }

    // The targets of out arguments (x for out var x; _ for out _ and for places that cannot be written)
    private static List<IlExpr> OutTargets(Node call)
    {
        var r = new List<IlExpr>();
        if (call.List == null) return r;
        foreach (var a in call.List)
        {
            if (!a.Has(Mods.Out)) continue;
            r.Add(new IlVar(OutArgumentName(a), null));
        }
        return r;
    }

    private static string OutArgumentName(Node arg)
    {
        var e = StripConv(arg.A!);
        if (e.Kind == NodeKind.PatDecl) return e.Local != null ? e.Local.IlName : "_";
        if (e.Kind == NodeKind.PatDiscard) return "_";
        if (e.Kind == NodeKind.Name) return e.Local != null ? e.Local.IlName : e.Text;
        return "_";
    }

    private IlExpr? BuildUserOutExpr(Node call, Symbol m)
    {
        var built = BuildInvocationCall(call);
        if (built == null) return null;
        var outs = OutTargets(call);
        var stats = new List<IlStat>();
        if (m.Type.Kind == TypeKind.Void)
        {
            stats.Add(new IlMultiAssign(outs, One(built), false, null));
            return new IlIife(stats, null);
        }
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(m.Type), null));
        stats.Add(new IlMultiAssign(targets, One(built), false, null));
        stats.Add(new IlReturn(new IlVar("__tcs_ret", null), null));
        return new IlIife(stats, null);
    }

    private bool BuildUserOutStatInto(Node call, Symbol m, IlPos? origin, List<IlStat> acc)
    {
        var built = BuildInvocationCall(call);
        if (built == null) return false;
        var outs = OutTargets(call);
        if (m.Type.Kind == TypeKind.Void)
        {
            acc.Add(new IlMultiAssign(outs, One(built), false, origin));
            return true;
        }
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(m.Type), null));
        stats.Add(new IlMultiAssign(targets, One(built), false, null));
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // out of --ref host methods (calls in receiver-member form): an IlMultiAssign like user-defined methods (targets are the return value, then the outs in order)
    private static bool IsRefOutCall(Node call)
    {
        var m = call.Sym;
        return m != null && m.Kind == SymKind.Method && m.HasOut && m.Builtin == "" && m.Owner != null && m.Owner.IsRef
            && call.A != null && call.A.Kind == NodeKind.Member;
    }

    private IlExpr? BuildRefMultiReturnValue(Node call, List<IlExpr> outTargets)
    {
        var m = call.Sym!;
        var callArgs = new List<IlExpr>();
        if (call.List != null)
        {
            foreach (var a in call.List)
            {
                if (a.Has(Mods.Out)) outTargets.Add(new IlVar(OutArgumentName(a), null));
                else if (a.Has(Mods.Ref)) return null;
                else
                {
                    var built = BuildExpr(a.A!);
                    if (built == null) return null;
                    callArgs.Add(built);
                }
            }
        }
        if (m.IsStatic) return new IlCall(TypeRef(m.Owner!) + "." + N(m), callArgs, null);
        var recv = BuildExpr(call.A!.A!);
        return recv == null ? null : new IlInvoke(recv, N(m), callArgs, null);
    }

    private IlExpr? BuildRefMultiReturnExpr(Node call)
    {
        var outs = new List<IlExpr>();
        var value = BuildRefMultiReturnValue(call, outs);
        if (value == null || outs.Count == 0) return null;
        // Ones without a value are only in statement position
        if (call.Sym!.Type.Kind == TypeKind.Void) return null;
        return new IlIife(RetMultiAssign(outs, value, call.Sym!.Type), null);
    }

    // The return-value local has a type (C and the VM receive the host function's return value in a variable of this type)
    private static List<IlStat> RetMultiAssign(List<IlExpr> outs, IlExpr value, TypeSym ret)
    {
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(ret), null));
        stats.Add(new IlMultiAssign(targets, One(value), false, null));
        stats.Add(new IlReturn(new IlVar("__tcs_ret", null), null));
        return stats;
    }

    private bool BuildRefMultiReturnStatInto(Node call, IlPos? origin, List<IlStat> acc)
    {
        var outs = new List<IlExpr>();
        var value = BuildRefMultiReturnValue(call, outs);
        if (value == null || outs.Count == 0) return false;
        if (call.Sym!.Type.Kind == TypeKind.Void)
        {
            acc.Add(new IlMultiAssign(outs, One(value), false, origin));
            return true;
        }
        acc.Add(new IlCallStat(new IlIife(RetMultiAssign(outs, value, call.Sym!.Type), null), origin));
        return true;
    }

    // with on a record / struct: writes the members on a duplicate. with on a struct writes on a copy of the value (nested structs are copied too)
    private IlExpr? BuildWithExpr(Node withExpr)
    {
        var src = BuildExpr(withExpr.A!);
        if (src == null) return null;
        src = CopyIfMutableStruct(NaturalType(withExpr.A!), src);
        var overrides = new List<IlOverride>();
        if (withExpr.B != null && withExpr.B.List != null)
        {
            foreach (var e in withExpr.B.List)
            {
                if (e.Kind != NodeKind.MemberInit || e.A == null) continue;
                var value = BuildExpr(e.A);
                if (value == null) return null;
                overrides.Add(new IlOverride(e.Sym != null ? N(e.Sym) : e.Text, WrapStructCopy(e.A, value)));
            }
        }
        return new IlWith(src, overrides, null);
    }

    private IlExpr? BuildInvocation(Node call)
    {
        if (call.Text == "with") return BuildWithExpr(call);
        var m = call.Sym;
        if (IsUserOutMethod(m)) return BuildUserOutExpr(call, m!);
        if (IsRefOutCall(call)) return BuildRefMultiReturnExpr(call);
        return BuildInvocationCall(call);
    }

    private IlExpr? BuildInvocationCall(Node call)
    {
        if (call.Info == CallKind.Nameof)
        {
            if (call.HasConst && call.Const != null) return IlLiterals.Str(call.Const.S);
            return Unported("nameof", call);
        }
        var m = call.Sym;
        var callee = call.A!;
        if (call != _condBinding && callee != _condBinding && (call.Value == 1 || (callee.Kind == NodeKind.Member && callee.Value == 1))) return Unported("conditional call", call);
        if (call.Info == CallKind.Delegate)
        {
            var args0 = BuildArgs(call, null);
            if (args0 == null) return null;
            // `f?.Invoke(args)`: calls the receiver of ?. (the delegate value)
            if (callee == _subNode && _subIl != null) return new IlDynCall(_subIl, args0, null);
            // Direct call of a delegate field / property
            if (callee.Kind == NodeKind.Member && callee.Sym != null && (callee.Sym.Kind == SymKind.Field || callee.Sym.Kind == SymKind.Property))
            {
                var recv0 = callee.Sym.IsStatic ? Ex(new IlVar(TypeRef(callee.Sym.Owner!), null)) : BuildExpr(callee.A!);
                if (recv0 == null) return null;
                if (callee.Sym.Kind == SymKind.Property && IsCustomProperty(callee.Sym))
                    return new IlDynCall(new IlInvoke(recv0, "get_" + N(callee.Sym), new List<IlExpr>(), null), args0, null);
                return new IlDynCall(new IlField(recv0, N(callee.Sym), null), args0, null);
            }
            if (callee.Kind == NodeKind.Name && callee.Sym != null && (callee.Sym.Kind == SymKind.Field || callee.Sym.Kind == SymKind.Property))
            {
                if (callee.Sym.Kind == SymKind.Property && IsCustomProperty(callee.Sym)) return null;
                IlExpr owner = callee.Sym.IsStatic ? new IlVar(TypeRef(callee.Sym.Owner!), null) : new IlVar("self", null);
                return new IlDynCall(new IlField(owner, N(callee.Sym), null), args0, null);
            }
            var fn = BuildExpr(callee);
            if (fn == null) return null;
            return new IlDynCall(fn, args0, null);
        }
        if (m == null) return Unported("call without symbol", call);
        var args = BuildArgs(call, m);
        if (args == null) return null;
        if (m.Builtin != "") return BuildBuiltinCall(call, m, args);
        args = WithOmittedDefaults(call, m, args);
        if (call.Info == CallKind.Extension)
        {
            // User extension method: a static call (the receiver is the first argument; a value-type receiver is a copy of the value)
            if (callee.Kind == NodeKind.Member && callee.A != null && callee.A.Info != RefKind.Type)
            {
                var extRecv = BuildExpr(callee.A);
                if (extRecv == null) return null;
                var all = new List<IlExpr>();
                all.Add(WrapStructCopy(callee.A, extRecv));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner!) + "." + N(m), all, null);
            }
            return new IlCall(TypeRef(m.Owner!) + "." + N(m), args, null);
        }
        if (callee.Kind == NodeKind.Member)
        {
            var recvNode = callee.A!;
            if (m.IsStatic)
            {
                // Calling an extension method in static form (`Agg.Total(xs, f)`): a static call of the type's function
                if (m.IsExtension && m.Owner != null) return new IlCall(TypeRef(m.Owner) + "." + N(m), args, null);
                var recv = BuildExpr(recvNode);
                if (recv == null) return null;
                return new IlDynCall(new IlField(recv, N(m), null), args, null);
            }
            var r = BuildExpr(recvNode);
            if (r == null) return null;
            // Struct instance members are free functions and are called statically (il-spec §10)
            if (IlTypes.IsUserStruct(recvNode.Type))
            {
                var all = new List<IlExpr>();
                all.Add(StructReceiverArg(recvNode, r));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner!) + "." + N(m), all, null);
            }
            return new IlInvoke(r, N(m), args, null);
        }
        if (callee.Kind == NodeKind.Name)
        {
            if (m.IsStatic) return new IlCall(TypeRef(m.Owner!) + "." + N(m), args, null);
            if (IlTypes.IsUserStruct(m.Owner!.SelfType()))
            {
                var all = new List<IlExpr>();
                all.Add(new IlVar("self", null));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner) + "." + N(m), all, null);
            }
            return new IlInvoke(new IlVar("self", null), N(m), args, null);
        }
        return Unported("callee", call);
    }

    // Call arguments (out is a multi-return target and does not become an argument)
    private List<IlExpr>? BuildArgs(Node call, Symbol? m)
    {
        var args = new List<IlExpr>();
        if (call.List == null) return args;
        for (int i = 0; i < call.List.Count; i++)
        {
            var arg = call.List[i];
            if (arg.Has(Mods.Out)) continue;
            if (arg.Has(Mods.Ref) || arg.Has(Mods.In)) return null;
            var built = BuildExpr(arg.A!);
            if (built == null) return null;
            args.Add(WrapArgumentCopy(m, call, i, built));
        }
        return args;
    }

    // Arguments are passed by value (the argument copy point of il-spec §10)
    private IlExpr WrapArgumentCopy(Symbol? callee, Node call, int index, IlExpr built)
    {
        var arg = call.List![index].A!;
        Node? receiver = call.Kind == NodeKind.Call && call.A != null && call.A.Kind == NodeKind.Member ? call.A.A : null;
        int offset = call.Kind == NodeKind.Call && call.Info == CallKind.Extension && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null && call.A.A.Info != RefKind.Type ? 1 : 0;
        if (CanShareArgument(callee, ArgExprs(call), offset, index, receiver)) return built;
        return WrapStructCopy(arg, built);
    }

    // C# determines the default values of omitted arguments at the caller, from the statically bound declaration
    private List<IlExpr> WithOmittedDefaults(Node call, Symbol m, List<IlExpr> args)
    {
        if (m.Owner == null || m.Owner.IsRef || m.Params == null) return args;
        var typeArgDefaults = TypeArgDefaultArgs(call, m);
        int given = args.Count;
        var filled = new List<IlExpr>();
        foreach (var a in args) filled.Add(a);
        int k = 0;
        // An extension method called in receiver form does not count the this parameter (Roslyn's reduced method)
        bool reduced = call.Info == CallKind.Extension && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null && call.A.A.Info != RefKind.Type;
        int pi = 0;
        foreach (var p in m.Params)
        {
            if (reduced && pi++ == 0) continue;
            if (p.IsOut) continue;
            if (k++ < given) continue;
            IlExpr? built = null;
            if (p.Default != null) built = ContainsTypeParam(p.Type) ? DefaultIl(p.Type.Subst(ClassTypeArgsOf(call, m), call.TypeArgs)) : BuildExpr(p.Default);
            if (built != null) filled.Add(built);
            else if (typeArgDefaults.Count > 0) filled.Add(IlLiterals.Null());
            else return args;
        }
        foreach (var t in typeArgDefaults) filled.Add(t);
        return filled;
    }

    private static bool ContainsTypeParam(TypeSym t)
    {
        return t.ContainsTypeParam();
    }

    // Hidden arguments on the call side. The default value for each type argument, in the same order as on the declaration side
    private List<IlExpr> TypeArgDefaultArgs(Node call, Symbol m)
    {
        var r = new List<IlExpr>();
        if ((m.Kind == SymKind.Ctor || m.IsStatic) && IlTypes.PassesTypeArgDefaults(m.Owner))
        {
            var classArgs = ClassTypeArgsOf(call, m);
            if (classArgs != null)
            {
                foreach (var a in classArgs) r.Add(TypeArgDefaultValue(a));
            }
        }
        if (m.TypeParams != null && m.TypeParams.Count > 0 && call.TypeArgs != null)
        {
            foreach (var a in call.TypeArgs) r.Add(TypeArgDefaultValue(a));
        }
        return r;
    }

    // Type arguments of the called member's type (the int of `Box<int>.Of`, its own type arguments for a call inside a generic type, the type of a new)
    private static List<TypeSym>? ClassTypeArgsOf(Node call, Symbol m)
    {
        if (m.Owner == null || m.Owner.TypeParams.Count == 0) return null;
        if (call.Kind == NodeKind.New) return call.Type != null ? call.Type.Args : null;
        var callee = call.A;
        if (callee != null && callee.Kind == NodeKind.Member && callee.A != null && callee.A.Type != null && callee.A.Type.Args != null) return callee.A.Type.Args;
        return m.Owner.SelfType().Args;
    }

    // Built-in methods (BCL)
    private IlExpr? BuildBuiltinCall(Node call, Symbol m, List<IlExpr> args)
    {
        var id = m.Builtin;
        var callee = call.A!;
        Node? recvNode = callee.Kind == NodeKind.Member && callee.A != null && callee.A.Info != RefKind.Type ? callee.A : null;
        TypeSym? recvType = recvNode != null ? recvNode.Type : null;
        if (callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A))
        {
            var facadeCallee = callee.A!.Type!.Name + "." + callee.Text;
            if (facadeCallee == "TinySystem.List.Sort") return SortCall(facadeCallee, call.List![0].A!.Type, args);
            return new IlCall(facadeCallee, args, null);
        }
        if (id == "Int.TryParse" || id == "Float.TryParse") return BuildNumericTryParse(call, id == "Int.TryParse" ? "Int" : "Float");
        if (id == "Dict.TryGetValue" && recvNode != null) return BuildDictTryGetValue(call, recvNode, m);
        // CompareTo of int / float (same type): an intrinsic that returns -1 / 0 / 1
        if (id == "Math.CompareTo" && recvNode != null)
            return BuildValueReceiverCall(call, recvNode, args, (r, a) => new IlCall("Math.CompareTo", Prepend(r, a), null));
        if (id == "Nullable.GetValueOrDefaultValue" && recvNode != null)
            return BuildValueReceiverCall(call, recvNode, args, (r, a) =>
            {
                // It is a method argument, so it is always evaluated once (not deferred, unlike ??)
                var stats = new List<IlStat>();
                stats.Add(new IlLocal("__tcs_val", r, null, null));
                stats.Add(new IlLocal("__tcs_fb", a[0], null, null));
                stats.Add(new IlReturn(new IlNullableGetOrDefault(new IlVar("__tcs_val", null), new IlVar("__tcs_fb", null), null), null));
                return new IlIife(stats, null);
            });
        switch (id)
        {
            case "Console.WriteLine":
            case "Console.Write":
                {
                    var printArgs = new List<IlExpr>();
                    for (int i = 0; i < args.Count; i++)
                    {
                        var src = call.List![i].A!;
                        printArgs.Add(StringifyForPrint(src, args[i]));
                    }
                    return new IlCall(id == "Console.Write" ? "__tcs_write" : "__tcs_writeline", printArgs, null);
                }
            case "Environment.GetEnvironmentVariable":
                return new IlCall("__tcs_getenv", args, null);
            case "String.IsNullOrEmpty":
            case "String.CompareOrdinal":
                return new IlCall("String." + callee.Text, args, null);
            case "String.Join":
            case "String.JoinAny":
            case "String.JoinArgs":
                {
                    if (args.Count == 2)
                    {
                        var elem = IlTypes.ElementOf(NaturalType(call.List![1].A!));
                        // Enum elements are converted to member names before Join (same as Enum.ToString)
                        var joinSpec = elem != null ? EnumFormatSpec(elem, false) : null;
                        if (joinSpec != null)
                        {
                            var ja = new List<IlExpr>();
                            ja.Add(args[0]);
                            ja.Add(args[1]);
                            ja.Add(IlLiterals.Str(joinSpec));
                            return new IlCall("String.JoinEnum", ja, null);
                        }
                    }
                    return new IlCall("String.Join", args, null);
                }
            case "Int.Parse":
                return new IlCall("__tcs_iparse", args, null);
            case "Float.Parse":
                return new IlCall("__tcs_fparse", args, null);
            case "Char.ToUpper":
            case "Char.ToLower":
            case "Char.ConvertFromUtf32":
                return new IlCall("Char." + callee.Text, args, null);
            // Static form of TinySystem.Text (Runes.RuneCount(s))
            case "Runes.RuneCountStatic":
            case "Runes.RuneSubstringStatic":
                return new IlCall("TinySystem.Utf8." + callee.Text, args, null);
            case "Math.Abs":
            case "Math.Min":
            case "Math.Max":
            case "Math.Atan2":
            case "Math.Clamp":
            case "Math.Pow":
            case "Math.Sign":
                return new IlCall(id, args, null);
            case "Math.Ceiling":
                return new IlCall("Math.Ceil", args, null);
        }
        if (id.StartsWith("Math.") && callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A))
            return new IlCall("TinySystem.Math." + callee.Text, args, null);
        // Statics of TinySystem facades (TinySystem.Random.Seed, TinySystem.List.Contains)
        if (id == "Random.Seed") return new IlCall("TinySystem.Random." + callee.Text, args, null);
        // Static methods of char
        if (id.StartsWith("Char.") && m.IsStatic) return new IlCall("Char." + callee.Text, args, null);
        if (id.StartsWith("Math.") && callee.Kind == NodeKind.Member)
            return new IlCall("Math." + (callee.Text == "Ceiling" ? "Ceil" : callee.Text), args, null);
        if (recvNode == null) return Unported("builtin static " + id, call);
        var recv = BuildExpr(recvNode);
        if (recv == null) return null;
        switch (id)
        {
            // TinySystem.Text methods are calls to the runtime's TinySystem.Utf8 (the receiver is the first argument)
            case "Runes.RuneCount":
            case "Runes.RuneSubstring":
            case "Utf8.Slice":
            case "Utf8.ToString":
                return new IlCall("TinySystem.Utf8." + callee.Text, Prepend(recv, args), null);
            case "List.Add":
                return ListAdd(recvType!, recv, args);
            case "List.RemoveAt":
                return new IlCall("List.RemoveAt", Two(recv, args[0]), null);
            case "List.Clear":
            case "Dict.Clear":
                return BuildClear(recv);
            case "List.Sort":
                return SortCall("List.Sort", recvType, Prepend(recv, args));
            case "List.Contains":
            case "List.IndexOf":
            case "List.Remove":
                return new IlCall("List." + callee.Text, Prepend(recv, WithStructElementOps(recvType, callee.Text, args)), null);
            case "List.FindIndex":
            case "List.RemoveAll":
            case "List.Insert":
                return new IlCall("List." + callee.Text, Prepend(recv, args), null);
            case "List.GetRange":
                {
                    var ga = Prepend(recv, args);
                    foreach (var c in ListElementCopier(recvType)) ga.Add(c);
                    return new IlCall("List.GetRange", ga, null);
                }
            case "Queue.Contains":
                return new IlCall("TcsQueue.Contains", Prepend(recv, WithStructElementOps(recvType, "Contains", args)), null);
            case "Dict.ContainsKey":
            case "Dict.Remove":
                return new IlCall("Dict." + callee.Text, Two(recv, args[0]), null);
            case "Queue.Enqueue":
            case "Queue.Dequeue":
            case "Queue.Clear":
                return new IlCall("TcsQueue." + callee.Text, Prepend(recv, args), null);
            case "Queue.Peek":
                return CopyIfMutableStruct(recvType!.Elem, new IlCall("TcsQueue.Peek", One(recv), null));
            case "HashSet.Add":
            case "HashSet.Remove":
            case "HashSet.Contains":
            case "HashSet.Clear":
                return new IlCall("TcsHashSet." + callee.Text, Prepend(recv, args), null);
            case "String.Contains":
            case "String.ContainsChar":
            case "String.Replace":
            case "String.ReplaceChar":
            case "String.Split":
            case "String.SplitChar":
            case "String.SplitWhite":
                return new IlCall("String." + callee.Text, Prepend(recv, WrapCharArgs(call, args)), null);
            case "String.StartsWith":
            case "String.EndsWith":
            case "String.StartsWithChar":
            case "String.EndsWithChar":
                return new IlCall("String." + callee.Text, Two(recv, WrapCharArgs(call, args)[0]), null);
            // Instance methods of TinySystem.Random
            case "Random.Next":
            case "Random.NextFloat":
            case "Random.Range":
                return new IlInvoke(recv, callee.Text, args, null);
            case "Nullable.GetValueOrDefault":
                return new IlNullableGetOrDefault(recv, DefaultIl(IlTypes.UnwrapNullable(recvType!)), null);
            case "String.Trim":
                return new IlCall("String.Trim", One(recv), null);
            case "String.ToUpper":
                return new IlCall("__tcs_upper", One(recv), null);
            case "String.ToLower":
                return new IlCall("__tcs_lower", One(recv), null);
            // ToString on a string is the receiver itself. A null receiver faults (null in interpolation and concatenation is the empty string, so __tcs_str is not used)
            case "String.ToString":
                return new IlCall("__tcs_nonnull", One(recv), null);
            case "Int.ToString":
            case "Float.ToString":
            case "Bool.ToString":
            case "Char.ToString":
            case "Object.ToString":
            case "Nullable.ToString":
            case "Rune.ToString":
            case "TypeParam.ToString":
                return WrapFloatToString(recvNode, recv);
            case "Enum.ToString":
                return WrapFloatToString(recvNode, recv);
            case "Enum.ToStringFormat":
                {
                    // Enum formats ("G" / "F" / "D")
                    var fa = call.List![0].A!;
                    if (fa.HasConst && fa.Const != null && fa.Const.Kind == ConstKind.String) return WrapEnumFormat(NaturalType(recvNode), recv, fa.Const.S);
                    return null;
                }
            case "Float.ToStringFormat":
            case "Int.ToStringFormat":
                {
                    // Standard numeric formats (same as `{x:F2}` in interpolation). An empty format is the same as no format
                    var fa = call.List![0].A!;
                    if (!(fa.HasConst && fa.Const != null && fa.Const.Kind == ConstKind.String)) return null;
                    if (fa.Const.S == "") return WrapFloatToString(recvNode, recv);
                    return WrapNumberFormat(NaturalType(recvNode), recv, fa.Const.S);
                }
        }
        return Unported("builtin " + id, call);
    }

    // The order of List.Sort without a comparison (and TinySystem.List.Sort) is .NET's default comparer. A uint enum is in unsigned order, so
    // it becomes a List.Sort that passes a closure with that comparison
    private static IlExpr SortCall(string callee, TypeSym? listType, List<IlExpr> args)
    {
        var elem = IlTypes.ElementOf(listType);
        if (args.Count != 1 || elem == null || elem.Kind != TypeKind.Enum || !IlTypes.IsUIntEnum(elem)) return new IlCall(callee, args, null);
        var parameters = new List<string>();
        parameters.Add("__tcs_a");
        parameters.Add("__tcs_b");
        var compare = new IlCall("Math.CompareTo", Two(FlipSignBit(new IlVar("__tcs_a", null)), FlipSignBit(new IlVar("__tcs_b", null))), null);
        return new IlCall("List.Sort", Two(args[0], new IlClosure(parameters, null, compare, new List<string>(), null)), null);
    }

    // A built-in method whose receiver is a value-type variable (a local / field / array element and the fields of that struct).
    // C# fixes the storage location of the receiver (a null object and an out-of-range index fault here), evaluates the arguments, and then calls with the current value of that variable (C# §12.6.6).
    // Only when the arguments may rewrite the storage location, it pins the reference-type prefix and the index in temps, reads the location once to check it, evaluates the arguments into temps, and reads the location again to call.
    // A property result, a List index, and a ?. receiver are copies of the value and are read before the arguments
    private IlExpr? BuildValueReceiverCall(Node call, Node recvNode, List<IlExpr> args, System.Func<IlExpr, List<IlExpr>, IlExpr?> build)
    {
        var argNodes = ArgExprs(call);
        bool argsWrite = false;
        foreach (var a in argNodes)
        {
            if (MayWriteStorage(a)) argsWrite = true;
        }
        var path = argsWrite && !(_condBinding != null && recvNode == _subNode) ? StructSlotPath(recvNode) : null;
        if (path == null)
        {
            var recv = BuildExpr(recvNode);
            return recv == null ? null : build(recv, args);
        }
        var stats = new List<IlStat>();
        IlExpr? root = path.IsVariable ? BuildExpr(path.Base) : FixSlot(path.Base, "__tcs_obj", argNodes, stats);
        if (root != null && path.Index != null)
        {
            var idx = FixSlot(path.Index, "__tcs_idx", argNodes, stats);
            root = idx != null ? new IlIndex(root, idx, true, IlTypes.HasNilableElements(NaturalType(path.Base)), null) : null;
        }
        var place = root == null ? null : RebuildSlot(recvNode, path.Stop, root);
        if (place == null) return null;
        if (!path.IsVariable) stats.Add(new IlLocal("_", place, null, null));
        var temps = new List<IlExpr>();
        for (int i = 0; i < args.Count; i++)
        {
            var name = "__tcs_arg" + i;
            stats.Add(new IlLocal(name, args[i], null, null));
            temps.Add(new IlVar(name, null));
        }
        var result = build(place, temps);
        if (result == null) return null;
        stats.Add(new IlReturn(result, null));
        return new IlIife(stats, null);
    }

    // A char argument of a string method is passed to the runtime as a one-character string (char is an integer code unit)
    private static List<IlExpr> WrapCharArgs(Node call, List<IlExpr> args)
    {
        var r = new List<IlExpr>();
        for (int i = 0; i < args.Count; i++)
        {
            if (call.List != null && i < call.List.Count && IlTypes.IsChar(NaturalType(call.List[i].A!))) r.Add(new IlCall("__tcs_cstr", One(args[i]), null));
            else r.Add(args[i]);
        }
        return r;
    }

    // The target of an out argument (the forward-declared local for out var x, the local itself for an existing local)
    private static IlExpr? OutTarget(Node arg)
    {
        var a = arg.A!;
        if (a.Kind == NodeKind.PatDecl) return new IlVar(a.Local != null ? a.Local.IlName : a.Text, null);
        if (a.Kind == NodeKind.PatDiscard) return new IlVar("_", null);
        if (a.Kind == NodeKind.Name) return new IlVar(a.Local != null ? a.Local.IlName : a.Text, null);
        return null;
    }

    // int.TryParse(s, out v) / float.TryParse: receives (found, value) from the runtime's Math.TryParseInt / TryParseFloat
    private IlExpr? BuildNumericTryParse(Node call, string kind)
    {
        if (call.Count != 2) return null;
        var textArg = call.List![0];
        var outArg = call.List[1];
        if (textArg.Has(Mods.Ref) || textArg.Has(Mods.Out)) return null;
        var text = BuildExpr(textArg.A!);
        if (text == null) return null;
        var target = OutTarget(outArg);
        if (target == null) return null;
        var stats = new List<IlStat>();
        var targets = Two(new IlVar("__tcs_found", null), new IlVar("__tcs_v", null));
        stats.Add(new IlMultiAssign(targets, One(new IlCall("Math.TryParse" + kind, Two(text, kind == "Float" ? IlLiterals.Float(0f) : IlLiterals.Int(0)), null)), true, null));
        stats.Add(new IlAssign(target, new IlVar("__tcs_v", null), null));
        stats.Add(new IlReturn(new IlVar("__tcs_found", null), null));
        return new IlIife(stats, null);
    }

    // Dictionary.TryGetValue(key, out v): a multi-return intrinsic (Dict.TryGet)
    private IlExpr? BuildDictTryGetValue(Node call, Node recvNode, Symbol m)
    {
        if (call.Count != 2) return null;
        var keyArg = call.List![0];
        var outArg = call.List[1];
        if (keyArg.Has(Mods.Ref) || keyArg.Has(Mods.Out)) return null;
        var key = BuildExpr(keyArg.A!);
        var recv = BuildExpr(recvNode);
        if (key == null || recv == null) return null;
        var target = OutTarget(outArg);
        if (target == null) return null;
        var dictType = NaturalType(recvNode);
        var valueType = dictType.Kind == TypeKind.Dict ? dictType.Arg(1) : null;
        var defaultValue = DefaultIl(valueType);
        var stats = new List<IlStat>();
        var targets = Two(new IlVar("__tcs_found", null), new IlVar("__tcs_v", null));
        var getArgs = new List<IlExpr>();
        getArgs.Add(recv);
        getArgs.Add(key);
        getArgs.Add(defaultValue);
        stats.Add(new IlMultiAssign(targets, One(new IlCall("Dict.TryGet", getArgs, null)), true, null));
        // The value written to out is a copy of the stored value
        stats.Add(new IlAssign(target, CopyIfMutableStruct(valueType, new IlVar("__tcs_v", null)), null));
        stats.Add(new IlReturn(new IlVar("__tcs_found", null), null));
        return new IlIife(stats, null);
    }

    // Stringification of the Console.WriteLine argument
    private IlExpr StringifyForPrint(Node src, IlExpr value)
    {
        var t = NaturalType(src);
        var user = UserToString(src, value);
        if (user != null) return user;
        var en = WrapEnumToString(t, value);
        if (en != null) return en;
        if (IsStringifiedByValue(t)) return StringifyByValue(t, value);
        if (IlTypes.IsFloating(t)) return new IlCall("__tcs_fstr", One(value), null);
        if (IlTypes.IsChar(t)) return new IlCall("__tcs_cstr", One(value), null);
        if (IlTypes.IsRune(t)) return RuneToString(value);
        if (t.Kind == TypeKind.Bool) return new IlCall("__tcs_bstr", One(value), null);
        return value;
    }

    // IL for List.Add
    private static IlCall ListAdd(TypeSym listType, IlExpr recv, List<IlExpr> args)
    {
        return new IlCall("List.Add", Prepend(recv, args), null);
    }

    // List.Clear / Dictionary.Clear: set every key to nil (the C and VM backends turn this shape into the runtime's clear)
    private static IlExpr BuildClear(IlExpr recv)
    {
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_obj", recv, null, null));
        var body = new IlBlock(One(new IlAssign(new IlIndex(new IlVar("__tcs_obj", null), new IlVar("k", null), false, false, null), IlLiterals.Null(), null)));
        stats.Add(new IlForPairs("k", null, new IlVar("__tcs_obj", null), body, null));
        return new IlIife(stats, null);
    }

    // Contains / IndexOf / Remove on struct elements use value equality. The per-type op_Equality is passed as the last argument
    private List<IlExpr> WithStructElementOps(TypeSym? listType, string methodName, List<IlExpr> args)
    {
        if (methodName != "Contains" && methodName != "IndexOf" && methodName != "Remove") return args;
        var elem = IlTypes.ElementOf(listType);
        if (!IlTypes.IsUserStruct(elem)) return args;
        var r = new List<IlExpr>();
        foreach (var a in args) r.Add(a);
        r.Add(new IlField(new IlVar(TypeName(elem!), null), "op_Equality", null));
        return r;
    }

    private static List<IlExpr> Prepend(IlExpr first, List<IlExpr> rest)
    {
        var r = new List<IlExpr>();
        r.Add(first);
        foreach (var x in rest) r.Add(x);
        return r;
    }

    // ---- Creation ----

    private IlExpr? BuildObjectCreation(Node creation)
    {
        var t = creation.Type;
        if (t == null) return Unported("new without type", creation);
        var init = creation.B;
        if (IlTypes.IsList(t))
        {
            if (creation.Count == 1)
            {
                // Copy constructor `new List<T>(source)`. For mutable struct elements, passes the per-type __copy
                var srcArg = creation.List![0];
                var st = NaturalType(srcArg.A!);
                if (init != null || srcArg.Has(Mods.Ref) || srcArg.Has(Mods.Out) || !(IlTypes.IsArray(st) || IlTypes.IsList(st) || IlTypes.IsDictCollection(st))) return null;
                var source = BuildExpr(srcArg.A!);
                if (source == null) return null;
                var cargs = One(source);
                foreach (var c in ListElementCopier(t)) cargs.Add(c);
                return new IlCall("List.Copy", cargs, null);
            }
            if (init == null) return new IlTable(new List<IlTableEntry>(), IlTypes.Display(t.Elem), null, false, null, null);
            var items = new List<IlTableEntry>();
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    var built = BuildExpr(e);
                    if (built == null) return null;
                    items.Add(new IlTableEntry(null, WrapStructCopy(e, built), null));
                }
            }
            return new IlTable(items, IlTypes.Display(t.Elem), null, false, null, null);
        }
        if (IlTypes.IsQueue(t)) return new IlNewQueue(IlTypes.Display(t), null);
        if (IlTypes.IsHashSet(t))
        {
            var created = new IlNewHashSet(IlTypes.Display(t), null);
            if (init == null) return created;
            // Add each item, as C# does
            var stats = new List<IlStat>();
            stats.Add(new IlLocal("__tcs_set", created, null, null));
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    var built = BuildExpr(e);
                    if (built == null) return null;
                    stats.Add(new IlCallStat(new IlCall("TcsHashSet.Add", Two(new IlVar("__tcs_set", null), built), null), null));
                }
            }
            stats.Add(new IlReturn(new IlVar("__tcs_set", null), null));
            return new IlIife(stats, null);
        }
        if (IlTypes.IsDict(t))
        {
            if (init == null) return new IlTable(new List<IlTableEntry>(), IlTypes.Display(t.Arg(1)), IlTypes.Display(t.Arg(0)), false, null, null);
            var entries = new List<IlTableEntry>();
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    Node keyNode;
                    Node valueNode;
                    if (e.Kind == NodeKind.ArrayInit && e.Count == 2)
                    {
                        keyNode = e.List![0];
                        valueNode = e.List[1];
                    }
                    else if (e.Kind == NodeKind.IndexInit)
                    {
                        keyNode = e.A!;
                        valueNode = e.B!;
                    }
                    else return null;
                    var key = BuildExpr(keyNode);
                    var value = BuildExpr(valueNode);
                    if (key == null || value == null) return null;
                    entries.Add(new IlTableEntry(key, WrapStructCopy(valueNode, value), null));
                }
            }
            return new IlTable(entries, IlTypes.Display(t.Arg(1)), IlTypes.Display(t.Arg(0)), false, null, null);
        }
        // `new T()` (T : new()) creates a new value from the received default-value descriptor
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_nt", One(passed), null);
            return Unported("new T()", creation);
        }
        if (t.Decl != null && t.Decl.IsRef)
        {
            // --ref types are named tables (ctor arguments are not supported)
            if (creation.Count > 0) return null;
            return BuildRefTypeTable(init, IlTypes.Display(t));
        }
        if (IlTypes.IsUtf8Bytes(t))
        {
            var uargs = new List<IlExpr>();
            if (creation.List != null)
            {
                foreach (var a in creation.List)
                {
                    var b = BuildExpr(a.A!);
                    if (b == null) return null;
                    uargs.Add(b);
                }
            }
            return new IlCall(uargs.Count == 0 ? "TinySystem.Utf8.Default" : "TinySystem.Utf8.New", uargs, null);
        }
        // Facade types (TinySystem.Random) are qualified so they do not clash with a user type of the same name
        if (t.Kind == TypeKind.Random)
        {
            var rargs = new List<IlExpr>();
            if (creation.List != null)
            {
                foreach (var a in creation.List)
                {
                    var b = BuildExpr(a.A!);
                    if (b == null) return null;
                    rargs.Add(b);
                }
            }
            return new IlNewObj("TinySystem.Random", rargs, "new", null);
        }
        if (t.Decl == null) return Unported("builtin type creation " + t.Display(), creation);
        var args = new List<IlExpr>();
        var ctor = creation.Sym;
        if (creation.List != null)
        {
            for (int i = 0; i < creation.List.Count; i++)
            {
                var a = creation.List[i];
                if (a.Has(Mods.Out) || a.Has(Mods.Ref) || a.Has(Mods.In)) return null;
                var built = BuildExpr(a.A!);
                if (built == null) return null;
                args.Add(WrapArgumentCopy(ctor, creation, i, built));
            }
        }
        bool hasCtorArgs = args.Count > 0;
        if (ctor != null && ctor.Params != null)
        {
            for (int i = args.Count; i < ctor.Params.Count; i++)
            {
                var p = ctor.Params[i];
                // The default value of a type containing type arguments is the default determined by the type arguments of the constructed type
                IlExpr? d = null;
                if (p.Default != null) d = ContainsTypeParam(p.Type) ? DefaultIl(p.Type.Subst(t.Args, null)) : BuildExpr(p.Default);
                if (d != null) args.Add(d);
                else args.Add(IlLiterals.Null());
            }
        }
        // new / ctor of a generic type receives the default values of the type arguments
        if (ctor != null && IlTypes.PassesTypeArgDefaults(t.Decl))
        {
            foreach (var a in TypeArgDefaultArgs(creation, ctor)) args.Add(a);
        }
        else if (ctor == null && IlTypes.PassesTypeArgDefaults(t.Decl) && t.Args != null)
        {
            foreach (var a in t.Args) args.Add(TypeArgDefaultValue(a));
        }
        var ctorName = ctor != null && ctor.Kind == SymKind.Ctor && ctor.Decl != null && ctor.Decl.Kind == NodeKind.Ctor ? IlNames.CtorName(ctor) : "ctor";
        IlExpr made;
        if (IlTypes.IsUserStruct(t) && hasCtorArgs) made = new IlCall(TypeName(t) + "." + ctorName, args, null);
        else made = new IlNewObj(TypeName(t), args, ctorName == "ctor" ? "new" : ctorName, null);
        if (init != null) return BuildObjectInitializerExpr(made, init, t);
        return made;
    }

    // Object initializer: lowers `new T(args) { M = v, Nested = { X = 1 }, Items = { a, b } }` into an IIFE.
    // A nested initializer assigns to / calls Add on the existing member, as in C# (no new)
    private IlExpr? BuildObjectInitializerExpr(IlExpr ctor, Node init, TypeSym? created)
    {
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_init", ctor, null, null));
        if (!AddInitializerStats(new IlVar("__tcs_init", null), init, stats, created)) return null;
        stats.Add(new IlReturn(new IlVar("__tcs_init", null), null));
        return new IlIife(stats, null);
    }

    // owner is the type of the value being initialized (used to map the type arguments of member types)
    private bool AddInitializerStats(IlExpr target, Node init, List<IlStat> stats, TypeSym? owner)
    {
        if (init.List == null) return true;
        foreach (var e in init.List)
        {
            if (e.Kind != NodeKind.MemberInit) return false;
            var initSym = e.Sym;
            TypeSym? memberType = initSym != null ? initSym.Type : null;
            if (memberType != null && owner != null && owner.Args != null && owner.Args.Count > 0) memberType = memberType.Subst(owner.Args, null);
            var initName = initSym != null ? N(initSym) : e.Text;
            bool customProp = initSym != null && initSym.Kind == SymKind.Property && IsCustomProperty(initSym);
            string? structOwner = customProp && IlTypes.IsUserStruct(initSym!.Owner!.SelfType()) ? TypeRef(initSym.Owner) : null;
            var rhsNode = e.A!;
            if (rhsNode.Kind == NodeKind.ObjectInit || rhsNode.Kind == NodeKind.CollectionInit)
            {
                IlExpr member = customProp ? PropGet(target, initName, false, structOwner, new List<IlExpr>()) : Ex(new IlField(target, initName, null));
                if (rhsNode.Kind == NodeKind.ObjectInit)
                {
                    if (!AddInitializerStats(member, rhsNode, stats, memberType)) return false;
                    continue;
                }
                if (!IlTypes.IsList(memberType)) return false;
                if (rhsNode.List != null)
                {
                    foreach (var item in rhsNode.List)
                    {
                        var built = BuildExpr(item);
                        if (built == null) return false;
                        stats.Add(new IlCallStat(ListAdd(memberType!, member, One(WrapStructCopy(item, built))), null));
                    }
                }
                continue;
            }
            var rhs = BuildExpr(rhsNode);
            if (rhs == null) return false;
            var value = TypeArgStoreCopy(memberType, WrapStructCopy(rhsNode, rhs));
            if (customProp) stats.Add(new IlCallStat(PropSet(target, initName, false, value, structOwner, new List<IlExpr>()), null));
            else stats.Add(new IlAssign(new IlField(target, initName, null), value, null));
        }
        return true;
    }

    private IlExpr? BuildRefTypeTable(Node? init, string type)
    {
        var entries = new List<IlTableEntry>();
        if (init == null || init.List == null) return new IlTable(entries, null, null, false, type, null);
        foreach (var e in init.List)
        {
            if (e.Kind != NodeKind.MemberInit || e.A == null || e.A.Kind == NodeKind.ObjectInit || e.A.Kind == NodeKind.CollectionInit) return null;
            var value = BuildExpr(e.A);
            if (value == null) return null;
            entries.Add(new IlTableEntry(null, WrapStructCopy(e.A, value), e.Sym != null ? N(e.Sym) : e.Text));
        }
        return new IlTable(entries, null, null, false, type, null);
    }

    // The per-type __copy for mutable struct elements, passed to List operations that copy elements (copy constructor, GetRange)
    private List<IlExpr> ListElementCopier(TypeSym? listType)
    {
        var r = new List<IlExpr>();
        var elem = IlTypes.ElementOf(listType);
        if (elem != null && IlTypes.IsMutableStruct(elem)) r.Add(new IlField(new IlVar(TypeName(elem), null), "__copy", null));
        return r;
    }

    // ---- Arrays ----

    private IlExpr? BuildNewArray(Node na)
    {
        var at = na.Type;
        var elem = at != null && at.Kind == TypeKind.Array ? at.Elem : null;
        var elemText = elem != null ? IlTypes.Display(elem) : null;
        if (na.C != null) return BuildArrayItems(na.C, elemText);
        if (na.B != null && elem != null)
        {
            var len = BuildExpr(na.B);
            if (len == null) return null;
            // Elements of a type-argument type get the received default value (a zero value per element for a struct descriptor)
            IlExpr? d = elem.Kind == TypeKind.TypeParam ? TypeArgDefaultValue(elem) : DefaultIl(elem);
            return new IlNewArray(elemText!, len, d, null);
        }
        return new IlTable(new List<IlTableEntry>(), elemText, null, true, null, null);
    }

    private IlExpr? BuildArrayItems(Node init, string? elementType)
    {
        var items = new List<IlTableEntry>();
        if (init.List != null)
        {
            foreach (var e in init.List)
            {
                var built = BuildExpr(e);
                if (built == null) return null;
                items.Add(new IlTableEntry(null, WrapStructCopy(e, built), null));
            }
        }
        return new IlTable(items, elementType, null, true, null, null);
    }

    // ---- Unary operations ----

    private IlExpr? BuildPrefixUnary(Node u)
    {
        var op = u.Text;
        if (op == "++" || op == "--") return BuildIncrementExpr(u.A!, op == "++", true);
        if (op == "!" && u.A != null && false) return null;
        var operand = BuildExpr(u.A!);
        if (operand == null) return null;
        if (op == "-" && u.Sym != null && u.Sym.Builtin == "")
        {
            var operands = new List<Node>();
            operands.Add(u.A!);
            var userNeg = TryBuildUserOperatorCall(u, operands, One(operand));
            if (userNeg != null) return userNeg;
        }
        if (IlTypes.IsNullableValueType(u.A!.Type))
        {
            if (op == "-") return new IlLiftedUn(IlUnOp.Neg, operand, null);
            if (op == "!") return new IlLiftedUn(IlUnOp.Not, operand, null);
            if (op == "~") return new IlLiftedUn(IlUnOp.BitNot, operand, null);
            return null;
        }
        if (op == "-" && u.HasConst && u.Const != null && u.Const.Kind == ConstKind.Int && u.Const.I == -2147483647 - 1) return IlLiterals.Int(-2147483647 - 1);
        if (op == "-" && _checked && u.Type != null && u.Type.Kind == TypeKind.Int) return new IlCall("__tcs_cneg", One(operand), null);
        if (op == "-") return new IlUn(IlUnOp.Neg, operand, null);
        if (op == "!") return new IlUn(IlUnOp.Not, operand, null);
        if (op == "~") return WrapToEnum(u.A!.Type, new IlUn(IlUnOp.BitNot, operand, null));
        if (op == "+") return operand;
        return null;
    }

    // ---- cast ----

    private IlExpr? BuildCast(Node cast)
    {
        var target = cast.Type!;
        var src = cast.B!;
        // For a cast that is an implicit conversion, the binder has already converted the expression to the target. Build from the original expression and type
        if (src.Kind == NodeKind.Conv && src.A != null && (src.Info == ConvKind.Identity || src.Info == ConvKind.IntToFloat || src.Info == ConvKind.NullableIntToFloat)) src = src.A;
        var srcType = src.Type;
        // Constant casts such as (int)'a' are folded
        if (cast.HasConst && cast.Const != null && cast.Const.Kind == ConstKind.Int && IlTypes.IsChar(srcType)) return IlLiterals.Int(cast.Const.I);
        if (IlTypes.IsChar(target) && srcType != null && (IlTypes.IsFloating(srcType) || IlTypes.IsIntegralSource(srcType) || srcType.Kind == TypeKind.Enum) && !IlTypes.IsChar(srcType))
        {
            var c = BuildExpr(src);
            if (c == null) return null;
            if (IlTypes.IsFloating(srcType)) c = new IlCall("__tcs_trunc", One(c), null);
            // The check is skipped when the source range is known (byte, a constant in 0..255)
            bool small = srcType.Kind == TypeKind.Byte || (cast.HasConst && cast.Const != null && cast.Const.Kind == ConstKind.Int && cast.Const.I >= 0 && cast.Const.I <= 255)
                || (src.HasConst && src.Const != null && src.Const.Kind == ConstKind.Float && src.Const.F > -1 && src.Const.F < 256);
            return small ? c : Ex(new IlCall("__tcs_char", One(c), null));
        }
        if (IlTypes.IsIntegral(target) && IlTypes.IsFloating(srcType))
        {
            var f = BuildExpr(src);
            return f == null ? null : new IlCall("__tcs_trunc", One(f), null);
        }
        var value = BuildExpr(src);
        if (value == null) return null;
        if (srcType == null) return value;
        if (target.Kind == TypeKind.Int || target.Kind == TypeKind.Float || target.Kind == TypeKind.Double)
        {
            bool runtimeTest = (srcType.IsReferenceLike || srcType.Kind == TypeKind.TypeParam) && srcType.Kind != TypeKind.Null;
            if (runtimeTest)
                return new IlNumericConvert(value, target.Kind == TypeKind.Int ? "int" : "float", true, null);
            bool toInt = target.Kind == TypeKind.Int;
            // T? is not a special type, so the conversion remains
            if (!toInt && IlTypes.IsFloating(srcType) && !IlTypes.IsNullableValueType(srcType)) return value;
            return new IlNumericConvert(value, toInt ? "int" : "float", false, null);
        }
        // (float?)x where x is int?: converts only when there is a value
        if (target.Kind == TypeKind.Nullable && target.Elem.Kind == TypeKind.Float && srcType.Kind == TypeKind.Nullable && IlTypes.IsIntegralSource(srcType.Elem))
            return new IlLiftedUn(IlUnOp.ToFloat, value, null);
        // (int?)x / (float?)x: an explicit T → U? conversion converts to U and then wraps (int → float? rounds to f32)
        if (target.Kind == TypeKind.Nullable && srcType.Kind != TypeKind.Nullable && srcType.Kind != TypeKind.Null)
        {
            if (target.Elem.Kind == TypeKind.Float && IlTypes.IsIntegralSource(srcType) && !IsFloatConverted(value))
                value = new IlNumericConvert(value, "float", false, null);
            return new IlNullableWrap(CastToEnum(target.Elem, srcType, value), IlTypes.Display(target.Elem), null);
        }
        if (target.Kind == TypeKind.Enum) return CastToEnum(target, srcType, value);
        // (E?)x where x is T?: wraps when there is a value (lifted)
        if (target.Kind == TypeKind.Nullable && srcType.Kind == TypeKind.Nullable && target.Elem.Kind == TypeKind.Enum && !TypeSym.Same(target.Elem, srcType.Elem))
            value = WrapToEnum(target, value);
        // A downcast to a user class is an IlCast (a runtime check point for the C backend; upcasts and same-type casts pass through)
        // The source type is a named type (object, interface, class, etc.; excluding type arguments and arrays; Roslyn's INamedTypeSymbol)
        bool namedSource = srcType.Kind != TypeKind.TypeParam && srcType.Kind != TypeKind.Array && srcType.Kind != TypeKind.Null;
        if (target.Kind == TypeKind.Class && target.Decl != null && namedSource && !TypeSym.Same(target, srcType) && !(srcType.Decl != null && IsDerivedFrom(srcType.Decl, target.Decl)))
            return new IlCast(value, CheckTypeName(target), null);
        if (!IsReferenceType(target) && target.Kind != TypeKind.Bool && target.Kind != TypeKind.Nullable) return value;
        // The `?` annotation on a cast type remains only in a #nullable enable context
        return new IlRefCast(value, IlTypes.Display(cast.File.NullableEnabled ? target : target.WithoutAnnotation()), null);
    }

    // An explicit conversion from an integer or another enum to an enum wraps the value into the range of the underlying type (unchanged from the same enum)
    private static IlExpr CastToEnum(TypeSym target, TypeSym srcType, IlExpr value)
    {
        if (target.Kind != TypeKind.Enum || TypeSym.Same(target, srcType)) return value;
        return WrapToEnum(target, value);
    }

    private static bool IsDerivedFrom(TypeDeclSym source, TypeDeclSym target)
    {
        TypeDeclSym? cur = source;
        while (cur != null)
        {
            if (cur == target) return true;
            cur = cur.BaseClass;
        }
        return false;
    }

    // `x as I` (interface): the value if the type check passes, nil otherwise
    private IlExpr? BuildAsExpr(Node asExpr)
    {
        var target = asExpr.Type;
        if (target == null || target.Kind != TypeKind.Interface) return Unported("as", asExpr);
        var operand = BuildExpr(asExpr.A!);
        if (operand == null) return null;
        var inputType = NaturalType(asExpr.A!);
        return BindPatternInput(operand, input =>
        {
            var check = BuildTypeCheck(input, inputType, target, null);
            if (check == null) return null;
            return new IlTernary(check, new IlRefCast(input, IlTypes.Display(target), null), IlLiterals.Null(), null);
        });
    }

    // ---- Indexing ----

    private IlExpr? BuildElementAccess(Node ea)
    {
        if (ea.Value == 1 && ea != _condBinding) return Unported("conditional index", ea);
        var recv = BuildExpr(ea.A!);
        var index = BuildExpr(ea.List![0]);
        if (recv == null || index == null) return null;
        var rt = ea.A!.Type;
        if (IlTypes.IsUtf8Bytes(rt)) return new IlCall("TinySystem.Utf8.At", Two(recv, index), null);
        bool plusOne = IlTypes.IsList(rt) || IlTypes.IsArray(rt);
        return new IlIndex(recv, index, plusOne, IlTypes.HasNilableElements(rt), null);
    }

    // ---- Nullable<T> (il-spec §3 / §13) ----

    private static bool IsNullableWrapped(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        return built is IlNullableWrap;
    }

    // If the target is T? and the expression itself is not T?, an IlNullableWrap (T is the inner type of the target). A null / default literal stays nil
    private IlExpr ApplyNullableConversion(Node conv, IlExpr built)
    {
        if (IsNullableWrapped(built)) return built;
        var target = conv.Type;
        var src = conv.A!.Type;
        if (target == null || !IlTypes.IsNullableValueType(target) || src == null || IlTypes.IsNullableValueType(src)
            || src.Kind == TypeKind.Null || src.Kind == TypeKind.Object || src.IsError)
            return built;
        if (StripConv(conv.A).Kind == NodeKind.DefaultLit && StripConv(conv.A).A == null) return built;
        // For a parenthesized expression, wrap the inside (Roslyn reports the same target type for the expression inside the parentheses too)
        return WrapInsideParen(built, target.Elem);
    }

    private static bool IsNullLiteral(Node e)
    {
        var x = StripConv(e);
        return x.Kind == NodeKind.NullLit || (x.Kind == NodeKind.DefaultLit && x.A == null);
    }

    private static IlLiftedOp? LiftedOpFor(string op, TypeSym? underlying)
    {
        switch (op)
        {
            case "+": return IlLiftedOp.Add;
            case "-": return IlLiftedOp.Sub;
            case "*": return IlLiftedOp.Mul;
            case "/": return IlTypes.IsIntegral(underlying) ? IlLiftedOp.DivInt : IlLiftedOp.DivFloat;
            case "%": return IlTypes.IsIntegral(underlying) ? IlLiftedOp.RemInt : IlLiftedOp.RemFloat;
            case "&": return underlying != null && underlying.Kind == TypeKind.Bool ? IlLiftedOp.And : IlLiftedOp.BitAnd;
            case "|": return underlying != null && underlying.Kind == TypeKind.Bool ? IlLiftedOp.Or : IlLiftedOp.BitOr;
            case "^": return IlLiftedOp.BitXor;
            case "<<": return IlLiftedOp.Shl;
            case ">>": return IlLiftedOp.Shr;
            case "==": return IlLiftedOp.Eq;
            case "!=": return IlLiftedOp.Ne;
            case "<": return IlLiftedOp.Lt;
            case "<=": return IlLiftedOp.Le;
            case ">": return IlLiftedOp.Gt;
            case ">=": return IlLiftedOp.Ge;
        }
        return null;
    }

    // The value of ++ / --: lifted Add / Sub for T?. An enum wraps into the range of its underlying type
    private static IlExpr StepValue(Node operand, IlExpr read, bool increment)
    {
        var type = NaturalType(operand);
        if (IlTypes.IsNullableValueType(type))
            return new IlLiftedBin(increment ? IlLiftedOp.Add : IlLiftedOp.Sub, read, new IlNullableWrap(IlLiterals.Int(1), "int", null), null);
        return WrapToEnum(type, new IlBin(increment ? IlBinOp.AddNum : IlBinOp.Sub, read, IlLiterals.Int(1), null));
    }

    // Wraps the value of an enum (and T? of an enum) into the range of its underlying type (same as C#'s unchecked conversion; only 8 / 16 bit underlying types change). A non-enum type is unchanged
    private static IlExpr WrapToEnum(TypeSym? type, IlExpr value)
    {
        var b = IlTypes.EnumUnderlying(type);
        if (b == null) return value;
        return WrapToInteger(b, value, IlTypes.IsNullableValueType(type));
    }

    // Wraps into the range of the integer type b. Unsigned keeps the low bits (`v & mask`), and signed sign-extends (`(v << s) >> s`).
    // lifted is a T? value (no value stays no value). An int literal is folded
    private static IlExpr WrapToInteger(TypeSym b, IlExpr value, bool lifted)
    {
        int bits = IlTypes.IntegerBits(b);
        if (bits == 32) return value;
        var lit = IntLiteral(value);
        if (!lifted && lit != null) return IlLiterals.Int(IlTypes.WrapInteger(b, lit.I));
        if (IlTypes.IsUnsignedInteger(b)) return IntOp(IlBinOp.BitAnd, value, (1 << bits) - 1, lifted);
        int shift = 32 - bits;
        return IntOp(IlBinOp.Shr, IntOp(IlBinOp.Shl, value, shift, lifted), shift, lifted);
    }

    // `value op k` (k is an int constant; op is BitAnd / Shl / Shr)
    private static IlExpr IntOp(IlBinOp op, IlExpr value, int k, bool lifted)
    {
        if (!lifted) return new IlParen(new IlBin(op, value, IlLiterals.Int(k), null), null);
        var lop = op == IlBinOp.BitAnd ? IlLiftedOp.BitAnd : op == IlBinOp.Shl ? IlLiftedOp.Shl : IlLiftedOp.Shr;
        return new IlParen(new IlLiftedBin(lop, value, new IlNullableWrap(IlLiterals.Int(k), "int", null), null), null);
    }

    // Ordering comparison of a uint enum: flipping the sign bit of both sides and comparing as signed gives the unsigned order
    private static IlExpr Relation(IlBinOp op, IlExpr left, IlExpr right, bool isUnsigned)
    {
        if (!isUnsigned) return new IlBin(op, left, right, null);
        return new IlBin(op, FlipSignBit(left), FlipSignBit(right), null);
    }

    private static IlExpr FlipSignBit(IlExpr value)
    {
        var lit = IntLiteral(value);
        if (lit != null) return IlLiterals.Int(lit.I ^ int.MinValue);
        return new IlParen(new IlBin(IlBinOp.BitXor, value, IlLiterals.Int(int.MinValue), null), null);
    }

    // An int literal inside parentheses (a foldable value). null when it is not a literal
    private static IlLit? IntLiteral(IlExpr value)
    {
        var inner = value;
        while (inner is IlParen p) inner = p.E;
        return inner is IlLit lit && lit.Kind == IlLitKind.Int ? lit : null;
    }

    // Comparison with null / ?? / lifted binary operations. null if not applicable (falls through to the normal path)
    private IlExpr? TryBuildNullableBinary(Node bin, IlExpr left, IlExpr right)
    {
        var op = bin.Text;
        var leftType = NaturalType(bin.A!);
        var rightType = NaturalType(bin.B!);
        // String concatenation is not lifted
        if (leftType.Kind == TypeKind.String || rightType.Kind == TypeKind.String || bin.Type!.Kind == TypeKind.String) return null;
        bool leftNullable = IlTypes.IsNullableValueType(leftType);
        bool rightNullable = IlTypes.IsNullableValueType(rightType);
        bool isEq = op == "==";
        bool isNe = op == "!=";
        if ((isEq || isNe) && leftNullable && IsNullLiteral(bin.B!))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(left, null), null) : new IlNullableHasValue(left, null);
        if ((isEq || isNe) && rightNullable && IsNullLiteral(bin.A!))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(right, null), null) : new IlNullableHasValue(right, null);
        // Comparison of a non-nullable value type with null (always false / true). The value is wrapped in T? for the comparison (for parentheses, the inside is wrapped)
        if ((isEq || isNe) && IsNullLiteral(bin.B!) && IsPlainValueType(leftType))
            return new IlBin(isEq ? IlBinOp.Eq : IlBinOp.Ne, WrapInsideParen(left, leftType), right, null);
        if ((isEq || isNe) && IsNullLiteral(bin.A!) && IsPlainValueType(rightType))
            return new IlBin(isEq ? IlBinOp.Eq : IlBinOp.Ne, left, WrapInsideParen(right, rightType), null);
        if (op == "??") return leftNullable ? new IlNullableGetOrDefault(left, right, null) : null;
        if (!leftNullable && !rightNullable) return null;
        var resultType = IlTypes.UnwrapNullable(bin.Type!);
        bool comparison = isEq || isNe || op == "<" || op == "<=" || op == ">" || op == ">=";
        var underlying = resultType.Kind == TypeKind.Bool && !comparison ? resultType : IlTypes.UnwrapNullable(leftNullable ? leftType : rightType);
        var lop = LiftedOpFor(op, underlying);
        if (lop == null) return null;
        // For a parenthesized expression, wrap the inside (Roslyn reports the same target type for the expression inside the parentheses too)
        var l2 = IsNullableWrapped(left) || leftNullable ? left : WrapInsideParen(left, leftType);
        var r2 = IsNullableWrapped(right) || rightNullable ? right : WrapInsideParen(right, rightType);
        return new IlLiftedBin(lop.Value, l2, r2, null);
    }

    private static bool IsPlainValueType(TypeSym t) =>
        t.Kind == TypeKind.Int || t.Kind == TypeKind.Float || t.Kind == TypeKind.Bool || t.Kind == TypeKind.Char || t.Kind == TypeKind.Byte
        || t.Kind == TypeKind.UInt || t.Kind == TypeKind.Short || t.Kind == TypeKind.Enum;

    private static IlExpr WrapInsideParen(IlExpr e, TypeSym t)
    {
        if (IsNullableWrapped(e)) return e;
        if (e is IlParen p) return new IlParen(WrapInsideParen(p.E, t), p.Origin);
        return new IlNullableWrap(e, IlTypes.Display(t), null);
    }

    // Dictionary.Add in statement position becomes an assignment to the key (IlAssign and IlIndex). It overwrites even if the key already exists; there is no fault corresponding to .NET's ArgumentException
    private bool BuildDictAddStatInto(Node call, IlPos? origin, List<IlStat> acc)
    {
        if (call.Sym == null || call.Sym.Builtin != "Dict.Add" || call.Count != 2) return false;
        var callee = call.A!;
        if (callee.Kind != NodeKind.Member || callee.A == null) return false;
        foreach (var a in call.List!)
        {
            if (a.Has(Mods.Ref) || a.Has(Mods.Out) || a.Has(Mods.In)) return false;
        }
        var recv = BuildExpr(callee.A);
        var key = BuildExpr(call.List![0].A!);
        var valueArg = call.List[1].A!;
        var value = BuildExpr(valueArg);
        if (recv == null || key == null || value == null) return false;
        acc.Add(new IlAssign(new IlIndex(recv, key, false, false, null), WrapStructCopy(valueArg, value), origin));
        return true;
    }

    // ---- Assignment ----

    private bool BuildAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var left = assign.A!;
        if (left.Kind == NodeKind.Name && left.Text == "_" && left.Info != RefKind.Local)
        {
            var discarded = BuildExpr(assign.B!);
            if (discarded == null) return false;
            acc.Add(new IlLocal("_", discarded, null, origin));
            return true;
        }
        if (left.Kind == NodeKind.Tuple) return UnportedStat("deconstruct assignment", assign);
        if (NeedsStructSlotAssign(assign)) return BuildStructSlotAssignInto(assign, origin, acc);
        if (IsCustomPropertyTarget(left)) return BuildPropAssignInto(assign, origin, acc);
        if (assign.Text == "??=") return BuildCoalesceAssignInto(assign, origin, acc);
        if (NeedsLoweredAssign(assign)) return BuildLoweredAssignInto(assign, origin, acc);
        if (assign.Text == "=")
        {
            var target = BuildExpr(left);
            var value = BuildExpr(assign.B!);
            if (target == null || value == null) return false;
            value = WrapStructCopy(assign.B!, value);
            if (target is IlField || target is IlIndex) value = TypeArgStoreCopy(NaturalType(left), value);
            // If target is a plain local, turn a conditional right side into an if statement
            if (value is IlTernary at && target is IlVar)
            {
                var thenB = new IlBlock(One(new IlAssign(target, at.T, null)));
                var elseB = new IlBlock(One(new IlAssign(target, at.F, null)));
                acc.Add(new IlIf(Arm(at.Cond, thenB), elseB, origin));
                return true;
            }
            // A shape with no value when nothing matches (`x = a?.b` with a null) stays an IIFE, because turning the if into assignments would leave the previous value
            if (value is IlIife ai && target is IlVar)
            {
                var shape = TryGetValueChainShape(ai);
                if (shape != null && shape.Complete)
                {
                    foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                    if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, target), origin));
                    else acc.Add(new IlAssign(target, shape.Tail!, origin));
                    return true;
                }
            }
            acc.Add(new IlAssign(target, value, origin));
            return true;
        }
        var op = CompoundOperator(assign);
        if (op == null) return false;
        var read = BuildExpr(left);
        var right = BuildExpr(assign.B!);
        if (read == null || right == null) return false;
        // `x op= a ⊕ b` is x = x op (a ⊕ b). The right side is parenthesized as one operand regardless of operator precedence
        if (right is IlBin || right is IlLiftedBin || right is IlTernary) right = new IlParen(right, null);
        IlExpr? applied;
        if (WritesLocalReadBy(left, assign.B!))
        {
            var r = right;
            applied = SnapshotOperand(read, l => BuildCompoundValue(assign, op, l, r));
        }
        else applied = BuildCompoundValue(assign, op, read, right);
        if (applied == null) return false;
        acc.Add(new IlAssign(read, applied, origin));
        return true;
    }

    // The spelling of compound assignment operators (`..` is concatenation, `~` is xor). += on string is `..`. &= / |= / ^= on bool are not handled (null)
    private static string? CompoundOperator(Node assign)
    {
        var lt = NaturalType(assign.A!);
        bool boolTarget = lt.Kind == TypeKind.Bool || (lt.Kind == TypeKind.Nullable && lt.Elem.Kind == TypeKind.Bool);
        switch (assign.Text)
        {
            case "+=": return lt.Kind == TypeKind.String ? ".." : "+";
            case "-=": return "-";
            case "*=": return "*";
            case "/=": return "/";
            case "%=": return "%";
            case "&=": return boolTarget ? null : "&";
            case "|=": return boolTarget ? null : "|";
            case "^=": return boolTarget ? null : "~";
            case "<<=": return "<<";
            case ">>=": return ">>";
        }
        return null;
    }

    private IlExpr? BuildCompoundValue(Node assign, string op, IlExpr read, IlExpr right)
    {
        // User operator for `x op= y`: it is in argument position, so the parentheses around the right side are dropped
        if (assign.Sym != null && assign.Sym.Builtin == "" && assign.Sym.Kind == SymKind.Method)
        {
            var operands = new List<Node>();
            operands.Add(assign.A!);
            operands.Add(assign.B!);
            var built = new List<IlExpr>();
            built.Add(read);
            built.Add(right is IlParen paren ? paren.E : right);
            var userOp = TryBuildUserOperatorCall(assign, operands, built);
            if (userOp != null) return userOp;
            return Unported("user compound operator", assign);
        }
        var type = NaturalType(assign.A!);
        if (IlTypes.IsNullableValueType(type))
        {
            var lifted = LiftedOpFor(op == "~" ? "^" : op, IlTypes.UnwrapNullable(type));
            if (lifted != null) return new IlLiftedBin(lifted.Value, read, right, null);
        }
        if (op == "/" && IlTypes.IsIntegral(type)) return new IlCall("__tcs_idiv", Two(read, right), null);
        if (op == "%" && IlTypes.IsIntegral(type)) return new IlCall("__tcs_irem", Two(read, right), null);
        if (op == "%" && IlTypes.IsFloating(type)) return new IlCall("__tcs_fmod", Two(read, right), null);
        if (op == "..")
            return new IlBin(IlBinOp.Concat, new IlParen(new IlBin(IlBinOp.Or, read, IlLiterals.Str(""), null), null), WrapConcatOperand(assign.B!, right), null);
        switch (op)
        {
            // += / -= on an enum wraps into the range of the underlying type
            case "+": return WrapToEnum(type, new IlBin(IlBinOp.AddNum, read, right, null));
            case "-": return WrapToEnum(type, new IlBin(IlBinOp.Sub, read, right, null));
            case "*": return new IlBin(IlBinOp.Mul, read, right, null);
            case "/": return new IlBin(IlBinOp.DivNum, read, right, null);
            case "%": return new IlBin(IlBinOp.RemNum, read, right, null);
            case "&": return new IlBin(IlBinOp.BitAnd, read, right, null);
            case "|": return new IlBin(IlBinOp.BitOr, read, right, null);
            case "~": return new IlBin(IlBinOp.BitXor, read, right, null);
            case "<<": return new IlBin(IlBinOp.Shl, read, right, null);
            case ">>": return new IlBin(IlBinOp.Shr, read, right, null);
        }
        return null;
    }

    private bool BuildIncrementInto(Node operand, bool increment, IlPos? origin, List<IlStat> acc)
    {
        if (IsCustomPropertyTarget(operand) || NeedsLoweredLvalue(operand)) return BuildLoweredIncrementInto(operand, increment, origin, acc);
        var target = BuildExpr(operand);
        if (target == null) return false;
        acc.Add(new IlAssign(target, StepValue(operand, target, increment), origin));
        return true;
    }

    // Whether it is an assignment to a custom property (through an accessor)
    private static bool IsCustomPropertyTarget(Node left)
    {
        var x = StripConv(left);
        if ((x.Kind == NodeKind.Name || x.Kind == NodeKind.Member) && x.Sym != null && x.Sym.Kind == SymKind.Property) return IsCustomProperty(x.Sym);
        return false;
    }

    // ---- Storage locations of assignment targets ----

    // Whether it is a reference type (Roslyn's IsReferenceType; type arguments are not treated as reference types regardless of constraints)
    private static bool IsReferenceType(TypeSym? t)
    {
        if (t == null || t.IsValueType) return false;
        switch (t.Kind)
        {
            case TypeKind.TypeParam:
            case TypeKind.Null:
            case TypeKind.Error:
            case TypeKind.Void:
            case TypeKind.MethodGroup:
            case TypeKind.StaticRef:
                return false;
        }
        return true;
    }

    // Syntax whose evaluation may have side effects (call, construction, ?., assignment, with, ++ / --)
    private static bool HasSideEffectSyntax(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Call:
            case NodeKind.New:
            case NodeKind.Assign:
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                return true;
            case NodeKind.Member:
            case NodeKind.Index:
                if (n.Value == 1) return true;
                break;
        }
        return AnyChild(n, c => HasSideEffectSyntax(c));
    }

    // Whether n contains a read of a custom property
    private static bool ReadsCustomProperty(Node n)
    {
        if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && n.Sym != null && n.Sym.Kind == SymKind.Property && IsCustomProperty(n.Sym)) return true;
        return AnyChild(n, c => ReadsCustomProperty(c));
    }

    // Whether evaluating the receiver / index (evaluated) of an lvalue has side effects. A custom property getter is a call, so it is included.
    // For a value-type receiver the temp becomes a copy and the update is lost, so the getter counts only when a reference-type prefix can be reached through struct fields / array elements
    private static bool HasLvalueSideEffect(Node evaluated, Node receiver)
    {
        if (HasSideEffectSyntax(evaluated)) return true;
        return (IsReferenceType(NaturalType(receiver)) || StructPlaceBase(receiver) != null) && ReadsCustomProperty(evaluated);
    }

    // The reference-type prefix reached by following a value-type place `prefix.f1.f2` / `prefix[i].f` through struct fields / array elements. null if it cannot be followed
    private static Node? StructPlaceBase(Node place)
    {
        while (true)
        {
            if (!NaturalType(place).IsValueType) return place;
            var p = StripConv(place);
            if (p.Kind == NodeKind.Paren) place = p.A!;
            else if (p.Kind == NodeKind.Member && p.A != null && p.Sym != null && p.Sym.Kind == SymKind.Field && !p.Sym.IsStatic && p.Info == RefKind.Field) place = p.A;
            else if (p.Kind == NodeKind.Index && p.Value != 1 && NaturalType(p.A!).Kind == TypeKind.Array) return p.A;
            else return null;
        }
    }

    // An lvalue whose receiver or index must be evaluated first (a receiver / index with side effects)
    private static bool NeedsLoweredLvalue(Node left)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member && x.A != null) return HasLvalueSideEffect(x.A, x.A);
        if (x.Kind == NodeKind.Index) return HasLvalueSideEffect(x, x.A!);
        return false;
    }

    private static Node? AssignReceiver(Node left)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member || x.Kind == NodeKind.Index) return x.A;
        return null;
    }

    // Whether the receiver / index of an assignment must be put in a temp. Besides lvalues with side effects, this applies when the receiver / index reads a local that the right side modifies.
    // Value-type receivers are excluded because the temp becomes a copy and the assignment is lost
    private bool NeedsLoweredAssign(Node assign)
    {
        if (assign.Text == "??=") return false;
        if (assign.Text != "=" && NeedsLoweredLvalue(assign.A!)) return true;
        var recv = AssignReceiver(assign.A!);
        return recv != null && IsReferenceType(NaturalType(recv)) && WritesLocalReadBy(assign.A!, assign.B!);
    }

    // Whether the right side of `??=` modifies a local read by the receiver / index
    private bool CoalesceRightWritesTarget(Node assign)
    {
        if (assign.Text != "??=") return false;
        var recv = AssignReceiver(assign.A!);
        return recv != null && IsReferenceType(NaturalType(recv)) && WritesLocalReadBy(assign.A!, assign.B!);
    }

    // An assignment target with the receiver / index fixed in temps (the setup statements and the location expression). force fixes them even without side effects
    private LoweredTarget? BuildLoweredTarget(Node left, bool force)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member && x.A != null && (force || HasLvalueSideEffect(x.A, x.A)))
        {
            var recv = BuildFixedReceiver(x.A);
            if (recv == null) return null;
            return new LoweredTarget(recv.Setup, new IlField(recv.Access, x.Sym != null ? N(x.Sym) : x.Text, null));
        }
        if (x.Kind == NodeKind.Index && (force || HasLvalueSideEffect(x, x.A!)))
        {
            var recv = BuildExpr(x.A!);
            var indexNode = x.List![0];
            var index = BuildExpr(indexNode);
            if (recv == null || index == null) return null;
            var rt = NaturalType(x.A!);
            bool plusOne = rt.Kind == TypeKind.List || rt.Kind == TypeKind.Array;
            var setup = new List<IlStat>();
            setup.Add(new IlLocal("__tcs_obj", recv, null, null));
            setup.Add(new IlLocal("__tcs_idx", index, null, null));
            return new LoweredTarget(setup, new IlIndex(new IlVar("__tcs_obj", null), new IlVar("__tcs_idx", null), plusOne, IlTypes.HasNilableElements(rt), null));
        }
        return null;
    }

    // Fixes the receiver into a place that is evaluated only once. A reference type goes into a temp as is; for a value type, only the reference-type prefix (and array indices) go into temps
    // and the field / element path is rebuilt (copying the struct would keep writes from reaching the original)
    private LoweredTarget? BuildFixedReceiver(Node recv)
    {
        var baseExpr = StructPlaceBase(recv);
        if (baseExpr != null && baseExpr != recv)
        {
            var built = BuildExpr(baseExpr);
            if (built == null) return null;
            var setup = new List<IlStat>();
            setup.Add(new IlLocal("__tcs_obj", built, null, null));
            var access = BuildStructPlace(recv, baseExpr, setup);
            return access == null ? null : new LoweredTarget(setup, access);
        }
        var whole = BuildExpr(recv);
        if (whole == null) return null;
        var s = new List<IlStat>();
        s.Add(new IlLocal("__tcs_obj", whole, null, null));
        return new LoweredTarget(s, new IlVar("__tcs_obj", null));
    }

    // Rebuilds the StructPlaceBase path from __tcs_obj (= baseExpr)
    private IlExpr? BuildStructPlace(Node place, Node baseExpr, List<IlStat> setup)
    {
        if (place == baseExpr) return new IlVar("__tcs_obj", null);
        var p = StripConv(place);
        if (p != place) return BuildStructPlace(p, baseExpr, setup);
        switch (p.Kind)
        {
            case NodeKind.Paren:
                return BuildStructPlace(p.A!, baseExpr, setup);
            case NodeKind.Member:
                {
                    var inner = BuildStructPlace(p.A!, baseExpr, setup);
                    return inner == null ? null : new IlField(inner, N(p.Sym!), null);
                }
            case NodeKind.Index:
                {
                    var index = BuildExpr(p.List![0]);
                    if (index == null) return null;
                    setup.Add(new IlLocal("__tcs_idx", index, null, null));
                    var at = NaturalType(p.A!);
                    return new IlIndex(new IlVar("__tcs_obj", null), new IlVar("__tcs_idx", null), true, IlTypes.HasNilableElements(at), null);
                }
        }
        return null;
    }

    // The assignment target of a custom property (receiver, name, whether static, the owning struct type (struct accessors are free-function calls), the default values of the type arguments of a static accessor).
    // fixUnstable also fixes in a temp a reference-type receiver that may change if read again (in expression position the setter is called after the right side)
    private PropTarget? BuildPropTarget(Node left, bool fixUnstable)
    {
        var x = StripConv(left);
        if (x.Sym == null || x.Sym.Kind != SymKind.Property || !IsCustomProperty(x.Sym)) return null;
        var prop = x.Sym;
        if (x.Kind == NodeKind.Name)
        {
            if (prop.IsStatic) return new PropTarget(new List<IlStat>(), new IlVar(TypeRef(prop.Owner!), null), N(prop), true, null, StaticAccessorArgs(prop, null));
            return new PropTarget(new List<IlStat>(), new IlVar("self", null), N(prop), false, IlTypes.IsUserStruct(prop.Owner!.SelfType()) ? TypeRef(prop.Owner) : null, new List<IlExpr>());
        }
        if (x.Kind != NodeKind.Member || x.A == null) return null;
        if (prop.IsStatic) return new PropTarget(new List<IlStat>(), new IlVar(TypeRef(prop.Owner!), null), N(prop), true, null, StaticAccessorArgs(prop, x.A));
        string? owner = IlTypes.IsUserStruct(NaturalType(x.A)) ? TypeRef(prop.Owner!) : null;
        if (HasLvalueSideEffect(x.A, x.A) || (fixUnstable && !IsStableRead(x.A) && IsReferenceType(NaturalType(x.A))))
        {
            var fixedRecv = BuildFixedReceiver(x.A);
            if (fixedRecv == null) return null;
            return new PropTarget(fixedRecv.Setup, fixedRecv.Access, N(prop), false, owner, new List<IlExpr>());
        }
        var recv = BuildExpr(x.A);
        if (recv == null) return null;
        return new PropTarget(new List<IlStat>(), recv, N(prop), false, owner, new List<IlExpr>());
    }

    private static IlExpr BuildPropGet(PropTarget p)
    {
        return PropGet(p.Recv, p.Name, p.IsStatic, p.StructOwner, p.StaticArgs);
    }

    private static IlExpr PropGet(IlExpr recv, string name, bool isStatic, string? structOwner, List<IlExpr> staticArgs)
    {
        if (structOwner != null) return new IlCall(structOwner + ".get_" + name, One(recv), null);
        if (isStatic) return new IlDynCall(new IlField(recv, "get_" + name, null), staticArgs, null);
        return new IlInvoke(recv, "get_" + name, new List<IlExpr>(), null);
    }

    private static IlExpr BuildPropSet(PropTarget p, IlExpr value)
    {
        return PropSet(p.Recv, p.Name, p.IsStatic, value, p.StructOwner, p.StaticArgs);
    }

    private static IlExpr PropSet(IlExpr recv, string name, bool isStatic, IlExpr value, string? structOwner, List<IlExpr> staticArgs)
    {
        if (structOwner != null) return new IlCall(structOwner + ".set_" + name, Two(recv, value), null);
        if (isStatic)
        {
            var args = new List<IlExpr>();
            args.Add(value);
            foreach (var a in staticArgs) args.Add(a);
            return new IlDynCall(new IlField(recv, "set_" + name, null), args, null);
        }
        return new IlInvoke(recv, "set_" + name, One(value), null);
    }

    // Assignment to a custom property in statement position
    private bool BuildPropAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var prop = BuildPropTarget(assign.A!, false);
        if (prop == null) return false;
        var right = BuildExpr(assign.B!);
        if (right == null) return false;
        IlStat body;
        bool needsWrap = prop.Setup.Count > 0;
        if (assign.Text == "=")
        {
            // The setter's value is an argument passed by value
            body = new IlCallStat(BuildPropSet(prop, WrapStructCopy(assign.B!, right)), null);
        }
        else if (assign.Text == "??=")
        {
            var setBody = new IlBlock(One(new IlCallStat(BuildPropSet(prop, WrapStructCopy(assign.B!, right)), null)));
            body = new IlIf(Arm(IsNullIl(BuildPropGet(prop), NaturalType(assign.A!)), setBody), null, null);
            // An if shape is always wrapped in an IIFE
            needsWrap = true;
        }
        else
        {
            var op = CompoundOperator(assign);
            if (op == null) return false;
            var applied = BuildCompoundValue(assign, op, BuildPropGet(prop), new IlParen(right, null));
            if (applied == null) return false;
            body = new IlCallStat(BuildPropSet(prop, applied), null);
        }
        if (!needsWrap)
        {
            acc.Add(IlTree.WithOrigin(body, origin));
            return true;
        }
        var stats = new List<IlStat>();
        foreach (var st in prop.Setup) stats.Add(st);
        stats.Add(body);
        acc.Add(new IlCallStat(new IlIife(stats, null), origin));
        return true;
    }

    // Assignment to a custom property in expression position: evaluates the receiver only once and uses the assigned value as the value of the expression (does not read the getter again).
    // The setter's value is an argument passed by value, so a copy is passed, and the value of the expression does not change even if the setter modifies value
    private IlIife? BuildPropAssignExpr(Node assign)
    {
        var prop = BuildPropTarget(assign.A!, true);
        if (prop == null) return null;
        var right = BuildExpr(assign.B!);
        if (right == null) return null;
        var stats = new List<IlStat>();
        foreach (var st in prop.Setup) stats.Add(st);
        var value = new IlVar("__tcs_v", null);
        var get = BuildPropGet(prop);
        var set = new IlCallStat(BuildPropSet(prop, CopyIfMutableStruct(NaturalType(assign.A!), value)), null);
        if (assign.Text == "=")
        {
            stats.Add(new IlLocal("__tcs_v", WrapStructCopy(assign.B!, right), null, null));
            stats.Add(set);
        }
        else if (assign.Text == "??=")
        {
            stats.Add(new IlLocal("__tcs_v", get, null, null));
            var body = new List<IlStat>();
            body.Add(new IlAssign(value, WrapStructCopy(assign.B!, right), null));
            body.Add(set);
            stats.Add(new IlIf(Arm(IsNullIl(value, NaturalType(assign.A!)), new IlBlock(body)), null, null));
        }
        else
        {
            var op = CompoundOperator(assign);
            if (op == null) return null;
            var applied = BuildCompoundValue(assign, op, get, new IlParen(right, null));
            if (applied == null) return null;
            stats.Add(new IlLocal("__tcs_v", applied, null, null));
            stats.Add(set);
        }
        stats.Add(new IlReturn(value, null));
        return new IlIife(stats, null);
    }

    // Null check: has-value for `T?`, comparison with nil for reference types
    private static IlExpr IsNullIl(IlExpr e, TypeSym? type)
    {
        if (IlTypes.IsNullableValueType(type)) return new IlUn(IlUnOp.Not, new IlNullableHasValue(e, null), null);
        return new IlBin(IlBinOp.Eq, e, IlLiterals.Null(), null);
    }

    // An IIFE that fixes the receiver / index in temps, then evaluates the right side, and returns the place after the assignment as the value
    private IlIife? BuildLoweredAssign(Node assign)
    {
        var lowered = BuildLoweredTarget(assign.A!, true);
        if (lowered == null) return null;
        var right = BuildExpr(assign.B!);
        if (right == null) return null;
        var stats = new List<IlStat>();
        foreach (var st in lowered.Setup) stats.Add(st);
        if (assign.Text == "??=")
        {
            var body = new IlBlock(One(new IlAssign(lowered.Access, WrapStructCopy(assign.B!, right), null)));
            stats.Add(new IlIf(Arm(IsNullIl(lowered.Access, NaturalType(assign.A!)), body), null, null));
            stats.Add(new IlReturn(lowered.Access, null));
            return new IlIife(stats, null);
        }
        IlExpr? applied;
        if (assign.Text == "=") applied = WrapStructCopy(assign.B!, right);
        else
        {
            var op = CompoundOperator(assign);
            applied = op == null ? null : BuildCompoundValue(assign, op, lowered.Access, new IlParen(right, null));
        }
        if (applied == null) return null;
        stats.Add(new IlAssign(lowered.Access, applied, null));
        stats.Add(new IlReturn(lowered.Access, null));
        return new IlIife(stats, null);
    }

    private bool BuildLoweredAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var iife = BuildLoweredAssign(assign);
        if (iife == null) return false;
        acc.Add(new IlCallStat(iife, origin));
        return true;
    }

    // The custom property / fixed lvalue path of ++ / --
    private bool BuildLoweredIncrementInto(Node operand, bool increment, IlPos? origin, List<IlStat> acc)
    {
        var prop = BuildPropTarget(operand, false);
        if (prop != null)
        {
            var body = new IlCallStat(BuildPropSet(prop, StepValue(operand, BuildPropGet(prop), increment)), null);
            if (prop.Setup.Count > 0)
            {
                var ps = new List<IlStat>();
                foreach (var st in prop.Setup) ps.Add(st);
                ps.Add(body);
                acc.Add(new IlDo(new IlBlock(ps), origin));
            }
            else acc.Add(new IlCallStat(body.Call, origin));
            return true;
        }
        var lowered = BuildLoweredTarget(operand, false);
        if (lowered == null) return false;
        var stats = new List<IlStat>();
        foreach (var st in lowered.Setup) stats.Add(st);
        stats.Add(new IlAssign(lowered.Access, StepValue(operand, lowered.Access, increment), null));
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // ??= (statement position, other than custom properties)
    private bool BuildCoalesceAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var built = BuildExpr(assign.B!);
        if (built == null) return false;
        var right = WrapStructCopy(assign.B!, built);
        var leftType = NaturalType(assign.A!);
        var lowered = BuildLoweredTarget(assign.A!, CoalesceRightWritesTarget(assign));
        if (lowered != null)
        {
            var stats = new List<IlStat>();
            foreach (var st in lowered.Setup) stats.Add(st);
            stats.Add(new IlIf(Arm(IsNullIl(lowered.Access, leftType), new IlBlock(One(new IlAssign(lowered.Access, right, null)))), null, null));
            acc.Add(new IlDo(new IlBlock(stats), origin));
            return true;
        }
        var left = BuildExpr(assign.A!);
        if (left == null) return false;
        acc.Add(new IlIf(Arm(IsNullIl(left, leftType), new IlBlock(One(new IlAssign(left, right, null)))), null, origin));
        return true;
    }

    // The value of ??= in expression position. If the left side is `T?`, the expression type is `T`, so it is the inner value of the place after the assignment
    private static IlExpr CoalesceAssignValue(Node assign, IlExpr assigned)
    {
        if (assign.Text == "??=" && IlTypes.IsNullableValueType(NaturalType(assign.A!)) && !IlTypes.IsNullableValueType(assign.Type))
            return new IlNullableValue(assigned, null);
        return assigned;
    }

    // The starting point reached by following the struct fields of a value-type place. null if it cannot be followed
    private static StructSlot? StructSlotPath(Node place)
    {
        while (true)
        {
            if (!NaturalType(place).IsValueType) return new StructSlot(place, place, null, false);
            var p = StripConv(place);
            switch (p.Kind)
            {
                case NodeKind.Paren:
                    place = p.A!;
                    continue;
                case NodeKind.Member:
                    if (p.A != null && p.Sym != null && p.Sym.Kind == SymKind.Field && !p.Sym.IsStatic && p.Info == RefKind.Field)
                    {
                        place = p.A;
                        continue;
                    }
                    if (p.Sym != null && p.Sym.Kind == SymKind.Field && p.Info == RefKind.Field) return new StructSlot(p, p, null, true);
                    return null;
                case NodeKind.Index:
                    if (p.Value != 1 && NaturalType(p.A!).Kind == TypeKind.Array) return new StructSlot(p, p.A!, p.List![0], false);
                    return null;
                case NodeKind.This:
                    return new StructSlot(p, p, null, true);
                case NodeKind.Name:
                    if (p.Info == RefKind.Local || (p.Info == RefKind.Field && p.Sym != null && p.Sym.Kind == SymKind.Field)) return new StructSlot(p, p, null, true);
                    return null;
            }
            return null;
        }
    }

    // Whether an assignment to a struct member (= / op= / ??=) must re-follow the path after the right side before writing. Not needed if the right side does not modify the storage location.
    // Not needed for a member directly under a local / parameter / this (`s.X`), because the variable itself is the storage location
    private bool NeedsStructSlotAssign(Node assign)
    {
        var ma = StripConv(assign.A!);
        if (ma.Kind != NodeKind.Member || ma.A == null || ma.Sym == null || ma.Sym.IsStatic) return false;
        if (ma.Sym.Kind != SymKind.Field && ma.Sym.Kind != SymKind.Property) return false;
        if (!NaturalType(ma.A).IsValueType) return false;
        var path = StructSlotPath(ma.A);
        if (path == null) return false;
        var stop = path.Stop;
        bool direct = path.IsVariable && Unparen(ma.A) == stop && (stop.Kind == NodeKind.This || (stop.Kind == NodeKind.Name && stop.Info == RefKind.Local));
        return !direct && MayWriteStorage(assign.B!);
    }

    private static Node Unparen(Node e)
    {
        var x = StripConv(e);
        return x.Kind == NodeKind.Paren ? Unparen(x.A!) : x;
    }

    // Statements that fix the reference-type prefix and array indices in temps (if they may change when read again), put the value in `__tcs_v`, and then write to the re-followed place
    private List<IlStat>? BuildStructSlotAssign(Node assign, bool asValue)
    {
        var ma = StripConv(assign.A!);
        var path = StructSlotPath(ma.A!)!;
        var stats = new List<IlStat>();
        var later = new List<Node>();
        later.Add(assign.B!);
        IlExpr? root = path.IsVariable ? BuildExpr(path.Base) : FixSlot(path.Base, "__tcs_obj", later, stats);
        if (root != null && path.Index != null)
        {
            var idx = FixSlot(path.Index, "__tcs_idx", later, stats);
            var bt = NaturalType(path.Base);
            root = idx != null ? new IlIndex(root, idx, true, IlTypes.HasNilableElements(bt), null) : null;
        }
        var right = BuildExpr(assign.B!);
        var recv = root == null ? null : RebuildSlot(ma.A!, path.Stop, root);
        if (recv == null || right == null) return null;
        var member = ma.Sym!;
        string? owner = member.Kind == SymKind.Property && IsCustomProperty(member) ? TypeRef(member.Owner!) : null;
        IlExpr read = owner != null ? PropGet(recv, N(member), false, owner, new List<IlExpr>()) : Ex(new IlField(recv, N(member), null));
        IlExpr v = new IlVar("__tcs_v", null);
        // Protects `__tcs_v`, used as the value of the expression, from the setter modifying value
        IlStat write;
        if (owner != null) write = new IlCallStat(PropSet(recv, N(member), false, asValue ? CopyIfMutableStruct(NaturalType(ma), v) : v, owner, new List<IlExpr>()), null);
        else write = new IlAssign(read, v, null);
        if (assign.Text == "??=")
        {
            stats.Add(new IlLocal("__tcs_v", read, null, null));
            var body = new List<IlStat>();
            body.Add(new IlAssign(v, WrapStructCopy(assign.B!, right), null));
            body.Add(write);
            stats.Add(new IlIf(Arm(IsNullIl(v, NaturalType(ma)), new IlBlock(body)), null, null));
            return stats;
        }
        IlExpr? value;
        if (assign.Text == "=") value = WrapStructCopy(assign.B!, right);
        else
        {
            var op = CompoundOperator(assign);
            value = op != null ? BuildCompoundValue(assign, op, read, new IlParen(right, null)) : null;
        }
        if (value == null) return null;
        stats.Add(new IlLocal("__tcs_v", value, null, null));
        stats.Add(write);
        return stats;
    }

    // Pins the prefix / index e of the storage location in a temp when reading it again could give a different value (later is the expressions evaluated after e)
    private IlExpr? FixSlot(Node e, string temp, List<Node> later, List<IlStat> stats)
    {
        var built = BuildExpr(e);
        if (built == null) return null;
        if (IsStableRead(e))
        {
            bool written = false;
            foreach (var n in later)
            {
                if (WritesLocalReadBy(e, n)) written = true;
            }
            if (!written) return built;
        }
        stats.Add(new IlLocal(temp, built, null, null));
        return new IlVar(temp, null);
    }

    private IlExpr? RebuildSlot(Node place, Node stop, IlExpr root)
    {
        if (place == stop) return root;
        var p = StripConv(place);
        if (p == stop) return root;
        if (p.Kind == NodeKind.Paren) return RebuildSlot(p.A!, stop, root);
        if (p.Kind == NodeKind.Member)
        {
            var inner = RebuildSlot(p.A!, stop, root);
            return inner == null ? null : new IlField(inner, N(p.Sym!), null);
        }
        return null;
    }

    private bool BuildStructSlotAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var stats = BuildStructSlotAssign(assign, false);
        if (stats == null) return false;
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // Assignment in expression position (`(i = y) >= 0`, `arr[x = 1]`): an IIFE that assigns as a statement and uses the assigned value as the value of the expression.
    // The value of a mutable struct is a copy, not the stored table. The value of ??= on a `T?` is the inner value
    private IlExpr? BuildAssignExpr(Node assign)
    {
        IlIife? iife;
        if (NeedsStructSlotAssign(assign))
        {
            var stats = BuildStructSlotAssign(assign, true);
            if (stats == null) iife = null;
            else
            {
                stats.Add(new IlReturn(new IlVar("__tcs_v", null), null));
                iife = new IlIife(stats, null);
            }
        }
        else if (IsCustomPropertyTarget(assign.A!)) iife = BuildPropAssignExpr(assign);
        else if (NeedsLoweredAssign(assign) || NeedsFixedAssignTarget(assign)) iife = BuildLoweredAssign(assign);
        else
        {
            var stats = new List<IlStat>();
            if (!BuildExprStatInto(assign, null, stats)) return null;
            var assigned = BuildExpr(assign.A!);
            if (assigned == null) iife = null;
            else
            {
                stats.Add(new IlReturn(assigned, null));
                iife = new IlIife(stats, null);
            }
        }
        if (iife == null) return null;
        int n = iife.Stats.Count;
        if (n == 0 || !(iife.Stats[n - 1] is IlReturn last) || last.Value == null) return iife;
        var rest = new List<IlStat>();
        for (int i = 0; i < n - 1; i++) rest.Add(iife.Stats[i]);
        rest.Add(new IlReturn(CopyIfMutableStruct(assign.Type, CoalesceAssignValue(assign, last.Value)), null));
        return iife with { Stats = rest };
    }

    // Whether getting the value of an assignment in expression position (reference-type receiver) by reading the left side again after the assignment would re-evaluate the receiver / index
    private bool NeedsFixedAssignTarget(Node assign)
    {
        if (NeedsLoweredLvalue(assign.A!) || CoalesceRightWritesTarget(assign)) return true;
        var x = StripConv(assign.A!);
        Node? recv = null;
        Node? index = null;
        if (x.Kind == NodeKind.Member) recv = x.A;
        else if (x.Kind == NodeKind.Index)
        {
            recv = x.A;
            index = x.List![0];
        }
        return recv != null && IsReferenceType(NaturalType(recv)) && StripConv(recv).Info != RefKind.Type
            && !(IsStableRead(recv) && (index == null || IsStableRead(index)));
    }

    // Expressions that give the same value when read again, as long as the right side does not modify them (local / parameter / this / type name / constants and operations on them)
    private static bool IsStableRead(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.BoolLit:
            case NodeKind.NullLit:
            case NodeKind.DefaultLit:
            case NodeKind.This:
                return true;
            case NodeKind.Paren:
            case NodeKind.Conv:
            case NodeKind.Cast:
                return IsStableRead(e.A!);
            case NodeKind.Binary:
                if (IsUserOperator(e.Sym)) return false;
                return IsStableRead(e.A!) && IsStableRead(e.B!);
            case NodeKind.Unary:
                if (e.Text != "-" && e.Text != "!" && e.Text != "~") return false;
                return IsStableRead(e.A!);
            case NodeKind.Name:
            case NodeKind.Member:
                if (e.Info == RefKind.Local || e.Info == RefKind.Type || e.Info == RefKind.Namespace || e.Info == RefKind.EnumMember) return true;
                return e.Info == RefKind.Field && e.Sym != null && e.Sym.IsConst;
        }
        return false;
    }

    private static bool IsUserOperator(Symbol? s)
    {
        return s != null && s.Kind == SymKind.Method && s.Builtin == "" && s.Owner != null && !s.Owner.IsRef;
    }

    // Whether evaluation may modify an existing storage location (field / array element / captured local)
    private bool MayWriteStorage(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                return true;
            case NodeKind.Binary:
                if (IsUserOperator(n.Sym)) return true;
                break;
            case NodeKind.Unary:
                if (IsUserOperator(n.Sym)) return true;
                break;
            case NodeKind.Call:
                if (n.Text == "with" || CallRunsUserCode(n)) return true;
                break;
            case NodeKind.New:
                if (NewRunsUserCode(n)) return true;
                break;
            case NodeKind.Name:
            case NodeKind.Member:
                if (n.Sym != null && n.Sym.Kind == SymKind.Property && IsCustomProperty(n.Sym)) return true;
                break;
        }
        return AnyChild(n, c => MayWriteStorage(c));
    }

    // Whether a call may modify state. Treated as not modifying: methods of Math / MathF / numbers / bool / char / string that take no delegate or out
    private bool CallRunsUserCode(Node call)
    {
        var m = call.Sym;
        if (m == null || m.Kind != SymKind.Method) return true;
        if (m.Params != null)
        {
            foreach (var p in m.Params)
            {
                if (p.IsOut || p.Type.Kind == TypeKind.Delegate) return true;
            }
        }
        if (m.Builtin == "") return true;
        var b = m.Builtin;
        var callee = call.A;
        if (b.StartsWith("Math.")) return callee != null && callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A);
        if (b.StartsWith("String.") || b.StartsWith("Float.") || b.StartsWith("Int.") || b.StartsWith("Char.") || b.StartsWith("Bool.")) return false;
        return true;
    }

    // Whether a construction may modify state. Constructors not in the source and implicit struct constructors (which only zero the fields) do not
    private static bool NewRunsUserCode(Node creation)
    {
        var t = creation.Type;
        if (t == null || t.Decl == null) return false;
        var ctor = creation.Sym;
        if (t.Decl.IsStruct && (ctor == null || ctor.Decl == null)) return false;
        return true;
    }

    // An assignment to a field / array element of a type-argument type copies when it is a struct descriptor (__tcs_tc)
    private IlExpr TypeArgStoreCopy(TypeSym? storage, IlExpr value)
    {
        if (storage != null && storage.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(storage);
            if (passed != null && !IsFreshStructIl(value)) return new IlCall("__tcs_tc", Two(passed, value), null);
        }
        return value;
    }

    // ---- for ----

    // The root of the function body (used to determine for ranges; inside a lambda, the lambda)
    public List<Node> ScopeRoots = new List<Node>();

    private bool BuildForInto(Node forStmt, List<IlStat> acc)
    {
        var origin = Origin(forStmt);
        var simple = TryBuildSimpleFor(forStmt);
        if (simple != null)
        {
            acc.Add(simple with { Origin = origin });
            return true;
        }
        Node? declaration = null;
        if (forStmt.List != null && forStmt.List.Count == 1 && forStmt.List[0].Kind == NodeKind.LocalDecl) declaration = forStmt.List[0];
        // A declared loop variable exists only inside the for (C# scope). A for with a declaration is wrapped in do..end
        var loop = declaration != null ? new List<IlStat>() : acc;
        if (declaration == null && forStmt.List != null)
        {
            foreach (var init in forStmt.List)
            {
                var e = init.Kind == NodeKind.ExprStmt ? init.A! : init;
                if (!BuildExprStatInto(e, forStmt, loop)) return false;
            }
        }
        if (declaration != null && declaration.List != null)
        {
            foreach (var v in declaration.List)
            {
                IlExpr? init = null;
                if (v.A != null)
                {
                    init = BuildExpr(v.A);
                    if (init == null) return false;
                }
                // Attach the declared type (`for (float y = -1; ...)` looks like int from the initial value)
                var name = v.Local != null ? v.Local.IlName : v.Text;
                loop.Add(new IlLocal(name, init, v.Local != null ? IlTypes.Display(v.Local.Type) : null, origin));
            }
        }
        IlExpr cond = IlLiterals.Bool(true);
        if (forStmt.A != null)
        {
            var built = BuildExpr(forStmt.A);
            if (built == null) return false;
            cond = built;
        }
        var body = BuildBlock(forStmt.B!);
        if (body == null) return false;
        var trailer = new List<IlStat>();
        if (forStmt.List2 != null)
        {
            foreach (var inc in forStmt.List2)
            {
                if (!BuildExprStatInto(inc, forStmt, trailer)) return false;
            }
        }
        loop.Add(new IlWhile(cond, body, new IlBlock(trailer), origin));
        if (loop != acc) acc.Add(new IlDo(new IlBlock(loop), origin));
        return true;
    }

    // Turns `for (int i = start; i < limit; i++)` (also `<=` and `i += 1`) into a numeric for.
    // Only when the control variable and the comparison are int and it can be shown that limit does not change during the for and that the body neither writes to the control variable nor captures it.
    // null (a general for) when it cannot be shown
    private IlNumericFor? TryBuildSimpleFor(Node forStmt)
    {
        if (forStmt.List == null || forStmt.List.Count != 1 || forStmt.List[0].Kind != NodeKind.LocalDecl) return null;
        var declNode = forStmt.List[0];
        if (declNode.List == null || declNode.List.Count != 1) return null;
        var decl = declNode.List[0];
        if (decl.A == null || decl.Local == null || !IsIntValueType(decl.Local.Type)) return null;
        var varName = decl.Text;
        var condNode = forStmt.A != null ? StripConv(forStmt.A) : null;
        if (condNode == null || condNode.Kind != NodeKind.Binary || (condNode.Text != "<" && condNode.Text != "<=")) return null;
        // Only comparisons between ints (a for that widens to float or T? to compare is a general for)
        if (!IsIntValueType(condNode.A!.Type) || !IsIntValueType(condNode.B!.Type)) return null;
        var condLeft = StripConv(condNode.A!);
        if (condLeft.Kind != NodeKind.Name || condLeft.Text != varName) return null;
        if (forStmt.Count2 != 1) return null;
        var inc = forStmt.List2![0];
        bool isIncByOne = (inc.Kind == NodeKind.PostIncDec && inc.Text == "++")
            || (inc.Kind == NodeKind.Assign && inc.Text == "+=" && StripConv(inc.B!).Kind == NodeKind.IntLit && SourceText(StripConv(inc.B!)) == "1");
        if (!isIncByOne) return null;
        // If the increment target is not the loop variable (`for (int i = 0; i < n; j++)`), a general for
        var incTarget = StripConv(inc.A!);
        if (incTarget.Kind != NodeKind.Name || incTarget.Text != varName || incTarget.Local != decl.Local) return null;
        if (!IsLoopInvariantBound(condNode.B!)) return null;
        if (IsAssignedWithin(forStmt.B!, varName)) return null;
        if (IsCapturedByLambdaWithin(forStmt.B!, varName)) return null;
        var start = BuildExpr(decl.A);
        var limit = BuildExpr(condNode.B!);
        var body = BuildBlock(forStmt.B!);
        if (start == null || limit == null || body == null) return null;
        return new IlNumericFor(decl.Local.IlName, start, limit, condNode.Text == "<=", body, null);
    }

    // Types whose IL value is an int (int, char, enum; T? is not included)
    private static bool IsIntValueType(TypeSym? t)
    {
        return t != null && (t.Kind == TypeKind.Int || t.Kind == TypeKind.Char || t.Kind == TypeKind.Enum);
    }

    private static string SourceText(Node n)
    {
        return n.File.Slice(n.Pos, n.End);
    }

    // Whether the upper bound of a for stays unchanged during the for: a constant, or a local variable that nothing writes to.
    // A local variable can also be written from another lambda that captured it, so even for a for inside a lambda the whole member body (including lambdas) is examined
    private bool IsLoopInvariantBound(Node bound)
    {
        var b = StripConv(bound);
        switch (b.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.CharLit:
                return true;
        }
        if (b.Kind != NodeKind.Name || b.Info != RefKind.Local || ScopeRoots.Count == 0) return false;
        return !IsAssignedWithin(ScopeRoots[0], b.Text);
    }

    // Whether name (an identifier with that spelling) is written in scope (the target of an assignment or a deconstructing assignment, ++ / --, a ref / out / in argument)
    private static bool IsAssignedWithin(Node scope, string name)
    {
        switch (scope.Kind)
        {
            case NodeKind.Assign:
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
                if (IsNameTarget(scope.A!, name)) return true;
                break;
            case NodeKind.DeconstructAssign:
                // The list of deconstruction targets (a nested deconstruction is seen as a child DeconstructAssign)
                if (scope.List == null) break;
                foreach (var target in scope.List)
                {
                    if (IsNameTarget(target, name)) return true;
                }
                break;
            case NodeKind.Arg:
                if ((scope.Has(Mods.Ref) || scope.Has(Mods.Out) || scope.Has(Mods.In)) && scope.A != null && IsNameTarget(scope.A, name)) return true;
                break;
        }
        return AnyChild(scope, c => IsAssignedWithin(c, name));
    }

    // Whether the write target is the variable name (also when parenthesized as `(name)`)
    private static bool IsNameTarget(Node target, string name)
    {
        var t = WithoutParens(StripConv(target));
        return t.Kind == NodeKind.Name && t.Text == name;
    }

    // Whether a lambda in scope contains name (an identifier with that spelling)
    private static bool IsCapturedByLambdaWithin(Node scope, string name)
    {
        if (scope.Kind == NodeKind.Lambda) return ContainsIdentifier(scope, name);
        return AnyChild(scope, c => IsCapturedByLambdaWithin(c, name));
    }

    private static bool ContainsIdentifier(Node n, string name)
    {
        if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member || n.Kind == NodeKind.TypeName) && n.Text == name) return true;
        return AnyChild(n, c => ContainsIdentifier(c, name));
    }

    private static bool AnyChild(Node n, System.Func<Node, bool> f)
    {
        if (n.A != null && f(n.A)) return true;
        if (n.B != null && f(n.B)) return true;
        if (n.C != null && f(n.C)) return true;
        if (n.D != null && f(n.D)) return true;
        if (n.List != null)
        {
            foreach (var x in n.List)
            {
                if (f(x)) return true;
            }
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2)
            {
                if (f(x)) return true;
            }
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3)
            {
                if (f(x)) return true;
            }
        }
        return false;
    }

    // ---- Forward declarations ----

    // Local variables declared before the statement: the out declaration forms (out var x / out _) and is-pattern designations not inside nested statements or lambdas.
    // Designations in the elseif conditions of an if are included too
    private List<Node> CollectPreDecls(Node stmt)
    {
        var outs = new List<Node>();
        CollectOutDecls(stmt, stmt, outs);
        var pats = new List<Node>();
        CollectIsDesignations(stmt, stmt, pats);
        var chain = stmt;
        while (chain.Kind == NodeKind.If && chain.C != null && chain.C.Kind == NodeKind.If)
        {
            CollectIsDesignations(chain.C.A!, chain.C.A!, pats);
            chain = chain.C;
        }
        var result = new List<Node>();
        var names = new List<string>();
        foreach (var d in outs)
        {
            var n = d.Local != null ? d.Local.IlName : "_";
            if (!names.Contains(n))
            {
                names.Add(n);
                result.Add(d);
            }
        }
        foreach (var d in pats)
        {
            var n = d.Local!.IlName;
            if (!names.Contains(n))
            {
                names.Add(n);
                result.Add(d);
            }
        }
        return result;
    }

    private static bool IsStatementKind(NodeKind k)
    {
        switch (k)
        {
            case NodeKind.Block:
            case NodeKind.LocalDecl:
            case NodeKind.DeconstructDecl:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.DoWhile:
            case NodeKind.For:
            case NodeKind.Foreach:
            case NodeKind.Break:
            case NodeKind.Continue:
            case NodeKind.Return:
            case NodeKind.Switch:
            case NodeKind.Empty:
                return true;
        }
        return false;
    }

    // Arguments in out declaration form (directly in this statement; not inside nested statements or lambdas)
    // This is decided by the statement nearest to the out argument, so an out var inside an expression-bodied lambda is also declared before this statement
    private void CollectOutDecls(Node n, Node stmt, List<Node> into)
    {
        if (n != stmt && IsStatementKind(n.Kind)) return;
        if (n.Kind == NodeKind.Arg && n.Has(Mods.Out) && n.A != null)
        {
            if (n.A.Kind == NodeKind.PatDecl) into.Add(n.A);
            else if (n.A.Kind == NodeKind.PatDiscard || (n.A.Kind == NodeKind.Name && n.A.Text == "_" && n.A.Info != RefKind.Local)) into.Add(n.A);
        }
        VisitChildren(n, c => CollectOutDecls(c, stmt, into));
    }

    // is-pattern designations (only declarations at the pattern root; not inside nested statements or lambdas)
    // scope itself is not counted (when the elseif condition itself is an is, its designation is not forward-declared)
    private void CollectIsDesignations(Node n, Node scope, List<Node> into)
    {
        if (n != scope && (IsStatementKind(n.Kind) || n.Kind == NodeKind.Lambda)) return;
        if (n != scope && n.Kind == NodeKind.Is && n.B != null && HasDesignation(n.B)) into.Add(n.B);
        VisitChildren(n, c => CollectIsDesignations(c, scope, into));
    }

    private static void VisitChildren(Node n, System.Action<Node> f)
    {
        if (n.A != null) f(n.A);
        if (n.B != null) f(n.B);
        if (n.C != null) f(n.C);
        if (n.D != null) f(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) f(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) f(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) f(x);
        }
    }

    // ---- foreach ----

    private bool BuildForeachInto(Node f, List<IlStat> acc)
    {
        var origin = Origin(f);
        if (f.Count > 0) return BuildForeachDeconstructInto(f, acc);
        var varName = f.Local != null ? f.Local.IlName : f.Text;
        var collNode = f.B!;
        var runesRecv = TryGetEnumerateRunesReceiver(collNode);
        if (runesRecv != null)
        {
            var str = BuildExpr(runesRecv);
            var runesBody = BuildBlock(f.C!);
            if (str == null || runesBody == null) return false;
            acc.Add(new IlForeachRunes(varName, str, runesBody, origin));
            return true;
        }
        var coll = BuildExpr(collNode);
        var body = BuildBlock(f.C!);
        if (coll == null || body == null) return false;
        // When the type of the variable differs from the type of the element, the element temp is iterated and the converted value goes into the variable at the head of the body
        if (f.D != null)
        {
            // The element is the local in the cast expression (inside the Conv the binder inserted, for an implicit conversion)
            var item = StripConv(f.D.B!).Local!;
            item.IlName = NewDeconstructTemp();
            var converted = BuildExpr(f.D);
            if (converted == null) return false;
            var stats = new List<IlStat>();
            stats.Add(new IlLocal(varName, CopyIfMutableStruct(f.Local!.Type, converted), IlTypes.Display(f.Local.Type), null));
            foreach (var st in body.Stats) stats.Add(st);
            body = new IlBlock(stats);
            varName = item.IlName;
        }
        var collType = NaturalType(collNode);
        // Only the Dictionary itself is iterated with IlForeachDict (KeyValuePair). For Keys / Values the runtime returns an array, so they use List iteration
        if (IlTypes.IsDict(collType))
        {
            acc.Add(new IlForeachDict(varName, coll, body, origin));
            return true;
        }
        // Queue / HashSet iterate over a snapshot of the current elements
        if (IlTypes.IsQueue(collType)) coll = new IlCall("TcsQueue.ToList", One(coll), null);
        if (IlTypes.IsHashSet(collType)) coll = new IlCall("TcsHashSet.ToList", One(coll), null);
        // The iteration variable is a copy of the element (changes to a struct element are not kept in the original)
        var iterVar = new IlVar(varName, null);
        var copied = f.D != null ? iterVar : CopyIfMutableStruct(f.Local != null ? f.Local.Type : null, iterVar);
        if (copied is IlStructCopy)
        {
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(iterVar, copied, null));
            foreach (var st in body.Stats) stats.Add(st);
            body = new IlBlock(stats);
        }
        acc.Add(new IlForeachList(varName, coll, body, origin));
        return true;
    }

    // For `s.EnumerateRunes()`, the receiver s
    private static Node? TryGetEnumerateRunesReceiver(Node expr)
    {
        var x = StripConv(expr);
        if (x.Kind != NodeKind.Call || x.A == null || x.A.Kind != NodeKind.Member || x.A.Text != "EnumerateRunes") return null;
        if (x.A.A == null || NaturalType(x.A.A).Kind != TypeKind.String) return null;
        return x.A.A;
    }

    // ---- Patterns ----

    private int _patternTempDepth;

    // Computes the input value only once and matches it. An input whose value or side effects may change when read again is evaluated into a temp before matching.
    // A field read is put in a temp if the match contains calls, because a getter or operator during the match may modify the field
    private IlExpr? BindPatternInput(IlExpr input, System.Func<IlExpr, IlExpr?> build)
    {
        if (input is IlLit || input is IlVar) return build(input);
        if (IlTree.IsCallFree(input))
        {
            var direct = build(input);
            if (direct == null || IlTree.IsCallFree(direct)) return direct;
        }
        var name = "__tcs_pat" + _patternTempDepth;
        _patternTempDepth++;
        var applied = build(new IlVar(name, null));
        _patternTempDepth--;
        if (applied == null) return null;
        var stats = new List<IlStat>();
        stats.Add(new IlLocal(name, input, null, null));
        stats.Add(new IlReturn(applied, null));
        return new IlIife(stats, null);
    }

    // Type reference: the IL type name for types declared in the source, the written name otherwise. null for types that cannot be expressed by a name alone
    private string? FormatTypeReference(Node typeNode, TypeSym? t)
    {
        if (t != null && t.Decl != null && !t.Decl.IsRef) return TypeName(t);
        if (typeNode.Kind == NodeKind.TypeName && typeNode.Count == 0) return typeNode.Text;
        if (typeNode.Kind == NodeKind.Name || typeNode.Kind == NodeKind.Member) return typeNode.Text;
        return null;
    }

    // The type of a primitive type test (null for other types)
    private static IlIsPrimType? PrimTypeTest(IlExpr expr, TypeSym? patternType)
    {
        if (patternType == null) return null;
        var t = IlTypes.UnwrapNullable(patternType);
        if (t.Kind == TypeKind.String) return new IlIsPrimType(expr, IlPrimType.String, null);
        if (t.Kind == TypeKind.Bool) return new IlIsPrimType(expr, IlPrimType.Bool, null);
        if (t.Kind == TypeKind.Enum || IlTypes.IsIntegral(t) || IlTypes.IsFloating(t)) return new IlIsPrimType(expr, IlPrimType.Number, null);
        return null;
    }

    private static bool IsStructuralType(TypeSym? t)
    {
        return IlTypes.IsArray(t) || IlTypes.IsList(t) || IlTypes.IsQueue(t) || IlTypes.IsHashSet(t) || IlTypes.IsDict(t);
    }

    // Type match. Matching a struct input (S / S?) against the same struct type only checks whether it has a value
    private IlExpr? BuildTypeCheck(IlExpr expr, TypeSym? inputType, TypeSym? patternType, string? typeRef)
    {
        // Matching a value-type input (T / T?) against the same value type T only checks whether it has a value (also for numbers, bool, and enum).
        // A T input always matches and the input is not read (BuildIsPattern keeps the evaluation of the left side of is)
        if (patternType != null && patternType.IsValueType && inputType != null)
        {
            if (TypeSym.Same(inputType, patternType)) return IlLiterals.Bool(true);
            if (IlTypes.IsNullableValueType(inputType) && TypeSym.Same(inputType.Elem, patternType)) return new IlNullableHasValue(expr, null);
        }
        var primTest = PrimTypeTest(expr, patternType);
        if (primTest != null) return primTest;
        if (IsStructuralType(patternType))
        {
            if (inputType != null && TypeSym.Same(inputType, patternType!)) return new IlBin(IlBinOp.Ne, expr, IlLiterals.Null(), null);
            return null;
        }
        if (patternType != null && patternType.Kind == TypeKind.Interface) return new IlIsType(expr, CheckTypeName(patternType), true, null);
        // A --ref class (tcs check and C stop with TCS1001, and the tcs VM decides by the type table; object-model §10.6) is referred to by the same IL type name as a cast
        if (patternType != null && patternType.Kind == TypeKind.Class && patternType.Decl != null && patternType.Decl.IsRef) return new IlIsType(expr, CheckTypeName(patternType), false, null);
        if (typeRef == null) return null;
        return new IlIsType(expr, typeRef, false, null);
    }

    private static IlBinOp RelationalIlOp(string op)
    {
        switch (op)
        {
            case ">": return IlBinOp.Gt;
            case ">=": return IlBinOp.Ge;
            case "<": return IlBinOp.Lt;
            case "<=": return IlBinOp.Le;
        }
        return IlBinOp.Eq;
    }

    private static IlExpr BinaryPatternIl(Node bp, IlExpr l, IlExpr r)
    {
        return new IlParen(new IlBin(bp.Kind == NodeKind.PatAnd ? IlBinOp.And : IlBinOp.Or, l, r, null), null);
    }

    // The input of the right side of `and` has the type narrowed by the left side. If the left side narrows an `S?` input to the struct S, the right side matches the inner value
    private static IlExpr AndRightInput(Node bp, IlExpr input, TypeSym? inputType)
    {
        if (bp.Kind == NodeKind.PatAnd && IlTypes.IsNullableValueType(inputType) && IlTypes.IsUserStruct(PatternNarrowedType(bp.A!, inputType)))
            return new IlNullableValue(input, null);
        return input;
    }

    private static TypeSym? NarrowedInput(Node bp, TypeSym? input)
    {
        if (bp.Kind == NodeKind.PatAnd) return PatternNarrowedType(bp.A!, input);
        return input;
    }

    // The type the input is narrowed to when the pattern matches (Roslyn's NarrowedType): that type for type / declaration patterns,
    // the written type for property patterns, or the inner type for a value-type T? input (it does not match null)
    private static TypeSym? PatternNarrowedType(Node p, TypeSym? input)
    {
        switch (p.Kind)
        {
            case NodeKind.PatType:
            case NodeKind.PatDecl:
                return p.Type;
            case NodeKind.PatProp:
                if (p.A != null) return p.Type;
                return input != null && IlTypes.IsNullableValueType(input) ? input.Elem : input;
        }
        return input;
    }

    // A constant pattern. C# compares the constant and the input with object.Equals, so a float NaN constant matches NaN (== is false between NaNs).
    // Whether the input is NaN is checked by its not being equal to itself (for a T? input, only when it has a value)
    private IlExpr? BuildConstantPattern(IlExpr input, Node constant, TypeSym? inputType)
    {
        if (constant.HasConst && constant.Const != null && constant.Const.IsNaN())
        {
            return BindPatternInput(input, x =>
            {
                if (!IlTypes.IsNullableValueType(inputType)) return new IlBin(IlBinOp.Ne, x, x, null);
                var value = new IlNullableValue(x, null);
                return new IlBin(IlBinOp.And, new IlNullableHasValue(x, null), new IlBin(IlBinOp.Ne, value, value, null), null);
            });
        }
        var v = BuildExpr(constant);
        return v == null ? null : new IlBin(IlBinOp.Eq, input, v, null);
    }

    // A type reference inside a constant pattern (Foo in `case Foo:` is a type)
    private static bool IsTypeConstant(Node p)
    {
        return p.Kind == NodeKind.PatType;
    }

    // Patterns of switch arms / cases
    private IlExpr? BuildPattern(Node pattern, IlExpr governing, TypeSym? inputType)
    {
        switch (pattern.Kind)
        {
            case NodeKind.PatConst:
                {
                    if (IsNullLiteral(pattern.A!) && IlTypes.IsNullableValueType(inputType))
                        return new IlUn(IlUnOp.Not, new IlNullableHasValue(governing, null), null);
                    return BuildConstantPattern(governing, pattern.A!, inputType);
                }
            case NodeKind.PatDiscard:
            case NodeKind.PatVar:
                return IlLiterals.Bool(true);
            case NodeKind.PatType:
                return BuildTypeCheck(governing, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            case NodeKind.PatDecl:
                {
                    if (IsStructDesignation(pattern, inputType))
                        return BuildStructDeclarationIs(new IlVar(pattern.Local!.IlName, null), governing, inputType!, pattern.Type!);
                    return BuildTypeCheck(governing, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
                }
            case NodeKind.PatProp:
                return BuildRecursivePattern(governing, pattern, inputType);
            case NodeKind.PatRel:
                {
                    var v = BuildExpr(pattern.A!);
                    return v == null ? null : Relation(RelationalIlOp(pattern.Text), governing, v, IlTypes.IsUIntEnum(pattern.A!.Type));
                }
            case NodeKind.PatAnd:
            case NodeKind.PatOr:
                return BindPatternInput(governing, input =>
                {
                    var l = BuildPattern(pattern.A!, input, inputType);
                    if (l == null) return null;
                    var r = BuildPattern(pattern.B!, AndRightInput(pattern, input, inputType), NarrowedInput(pattern, inputType));
                    return r == null ? null : BinaryPatternIl(pattern, l, r);
                });
            case NodeKind.PatNot:
                {
                    var sub = BuildIsSubPattern(governing, pattern.A!, inputType);
                    return sub == null ? null : new IlUn(IlUnOp.Not, new IlParen(sub, null), null);
                }
        }
        return Unported("pattern", pattern);
    }

    // Sub-patterns of an is expression
    private IlExpr? BuildIsSubPattern(IlExpr expr, Node pattern, TypeSym? inputType)
    {
        switch (pattern.Kind)
        {
            case NodeKind.PatConst:
                return BuildConstantPattern(expr, pattern.A!, inputType);
            case NodeKind.PatType:
            case NodeKind.PatDecl:
                return BuildTypeCheck(expr, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            case NodeKind.PatNot:
                {
                    var sub = BuildIsSubPattern(expr, pattern.A!, inputType);
                    return sub == null ? null : new IlUn(IlUnOp.Not, new IlParen(sub, null), null);
                }
            case NodeKind.PatRel:
                {
                    var v = BuildExpr(pattern.A!);
                    return v == null ? null : Relation(RelationalIlOp(pattern.Text), expr, v, IlTypes.IsUIntEnum(pattern.A!.Type));
                }
            case NodeKind.PatAnd:
            case NodeKind.PatOr:
                return BindPatternInput(expr, input =>
                {
                    var l = BuildIsSubPattern(input, pattern.A!, inputType);
                    if (l == null) return null;
                    var r = BuildIsSubPattern(AndRightInput(pattern, input, inputType), pattern.B!, NarrowedInput(pattern, inputType));
                    return r == null ? null : BinaryPatternIl(pattern, l, r);
                });
            case NodeKind.PatProp:
                return BuildRecursivePattern(expr, pattern, inputType);
            case NodeKind.PatDiscard:
            case NodeKind.PatVar:
                return null;
        }
        return Unported("is sub-pattern", pattern);
    }

    // A property pattern in a nested position. The form with a variable binds only at the root position (the right side of is, a switch arm and case), so it is not built here
    private IlExpr? BuildRecursivePattern(IlExpr expr, Node rp, TypeSym? inputType)
    {
        if (rp.Local != null) return null;
        return BuildPropertyMatch(expr, rp, inputType);
    }

    // The match of a property pattern (the caller binds the variable)
    private IlExpr? BuildPropertyMatch(IlExpr expr, Node rp, TypeSym? inputType)
    {
        var subpatterns = new List<Node>();
        if (rp.List != null)
        {
            foreach (var sub in rp.List)
            {
                if (sub.Text != "") subpatterns.Add(sub);
            }
        }
        var patternType = rp.A == null ? null : rp.Type;
        bool nullableValue = IlTypes.IsNullableValueType(inputType) && (patternType == null || TypeSym.Same(inputType!.Elem, patternType));
        bool nullCheck = rp.A == null && inputType != null && !inputType.IsValueType;
        int inputReads = subpatterns.Count + (rp.A != null || nullableValue || nullCheck ? 1 : 0);
        if (inputReads > 1) return BindPatternInput(expr, input => BuildRecursiveCore(input, rp, inputType, patternType, nullableValue, nullCheck, subpatterns));
        return BuildRecursiveCore(expr, rp, inputType, patternType, nullableValue, nullCheck, subpatterns);
    }

    private IlExpr? BuildRecursiveCore(IlExpr input, Node rp, TypeSym? inputType, TypeSym? patternType, bool nullableValue, bool nullCheck, List<Node> subpatterns)
    {
        var conditions = new List<IlExpr>();
        if (nullableValue) conditions.Add(new IlNullableHasValue(input, null));
        else if (rp.A != null)
        {
            var check = BuildTypeCheck(input, inputType, patternType, FormatTypeReference(rp.A, patternType));
            if (check == null) return null;
            conditions.Add(check);
        }
        else if (nullCheck) conditions.Add(new IlBin(IlBinOp.Ne, input, IlLiterals.Null(), null));
        // This is after the type check passed, so the member is read as the checked type
        IlExpr recv = input;
        if (nullableValue) recv = new IlNullableValue(input, null);
        else if (patternType != null && patternType.IsReferenceLike && subpatterns.Count > 0 && (inputType == null || !TypeSym.Same(inputType, patternType)))
            recv = new IlRefCast(input, IlTypes.Display(patternType), null);
        foreach (var sub in subpatterns)
        {
            var sym = sub.Sym;
            var propName = sym != null ? N(sym) : sub.Text;
            IlExpr propExpr = sym != null && sym.Kind == SymKind.Property && IsCustomProperty(sym)
                ? new IlInvoke(recv, "get_" + propName, new List<IlExpr>(), null)
                : new IlField(recv, propName, null);
            var cond = BuildIsSubPattern(propExpr, sub.A!, sub.Type);
            if (cond == null) return null;
            conditions.Add(cond);
        }
        if (conditions.Count == 0) return IlLiterals.Bool(true);
        var result = conditions[0];
        for (int i = 1; i < conditions.Count; i++) result = new IlBin(IlBinOp.And, result, conditions[i], null);
        return result;
    }

    // A pattern with a variable at its root: a declaration pattern (`T v`) and a property pattern with a variable (`T { ... } v`, `{ ... } v`)
    private static bool HasDesignation(Node p)
    {
        return (p.Kind == NodeKind.PatDecl || p.Kind == NodeKind.PatProp) && p.Local != null;
    }

    // A variable of struct S applied to an S / S? input, and a variable of value type T applied to a T? input (extracts the value and binds it)
    private static bool IsStructDesignation(Node dp, TypeSym? inputType)
    {
        var t = dp.Local != null ? dp.Local.Type : dp.Type;
        if (t == null || inputType == null || !TypeSym.Same(IlTypes.UnwrapNullable(inputType), t)) return false;
        return IlTypes.IsUserStruct(t) || t.IsValueType && IlTypes.IsNullableValueType(inputType);
    }

    private IlExpr DesignationValue(IlExpr input, TypeSym inputType, TypeSym declType)
    {
        IlExpr value = IlTypes.IsNullableValueType(inputType) ? Ex(new IlNullableValue(input, null)) : input;
        if (!IlTypes.IsMutableStruct(declType)) return value;
        return new IlStructCopy(value, TypeName(declType), false, null);
    }

    // `x is S v` (x: S / S?): if there is a value, binds a copy and yields true
    private IlExpr BuildStructDeclarationIs(IlVar target, IlExpr expr, TypeSym inputType, TypeSym declType)
    {
        if (!IlTypes.IsNullableValueType(inputType))
        {
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(target, DesignationValue(expr, inputType, declType), null));
            stats.Add(new IlReturn(IlLiterals.Bool(true), null));
            return new IlIife(stats, null);
        }
        return BindPatternInput(expr, input =>
        {
            var inner = new List<IlStat>();
            inner.Add(new IlAssign(target, DesignationValue(input, inputType, declType), null));
            inner.Add(new IlReturn(IlLiterals.Bool(true), null));
            var stats = new List<IlStat>();
            stats.Add(new IlIf(Arm(new IlNullableHasValue(input, null), new IlBlock(inner)), null, null));
            stats.Add(new IlReturn(IlLiterals.Bool(false), null));
            return new IlIife(stats, null);
        })!;
    }

    // Forward declarations of switch designations (a class binds the input as is; a struct only declares the type)
    private IlLocal SwitchDesignationLocal(Node dp, TypeSym? inputType, IlExpr sw)
    {
        var name = dp.Local!.IlName;
        if (IsStructDesignation(dp, inputType)) return new IlLocal(name, null, IlTypes.Display(dp.Local.Type), null);
        return new IlLocal(name, sw, IlTypes.Display(dp.Local.Type), null);
    }

    // The root pattern of a switch arm and case. A property pattern with a variable matches, for a reference type, against the variable the predeclaration initialized with the input, and
    // for a value type binds the value after the match succeeds
    private IlExpr? BuildSwitchPattern(Node pattern, IlExpr sw, TypeSym? inputType)
    {
        if (pattern.Kind != NodeKind.PatProp || pattern.Local == null) return BuildPattern(pattern, sw, inputType);
        if (IsStructDesignation(pattern, inputType)) return BuildStructPropertyDesignation(pattern, sw, inputType!);
        return BuildPropertyMatch(sw, pattern, inputType);
    }

    // A property pattern with a value-type variable: when the match succeeds, binds the value (a copy for a struct) to the variable
    private IlExpr? BuildStructPropertyDesignation(Node pattern, IlExpr input, TypeSym inputType)
    {
        var matched = BuildPropertyMatch(input, pattern, inputType);
        if (matched == null) return null;
        var bind = BuildStructDeclarationIs(new IlVar(pattern.Local!.IlName, null), input, inputType, pattern.Local.Type);
        return new IlBin(IlBinOp.And, matched, bind, null);
    }

    private IlExpr? BuildIsPattern(Node isNode)
    {
        var expr = BuildExpr(isNode.A!);
        if (expr == null) return null;
        var inputType = NaturalType(isNode.A!);
        var pattern = isNode.B!;
        if (HasDesignation(pattern))
        {
            var name = pattern.Local!.IlName;
            if (IsStructDesignation(pattern, inputType))
            {
                if (pattern.Kind == NodeKind.PatDecl) return BuildStructDeclarationIs(new IlVar(name, null), expr, inputType, pattern.Type!);
                return BindPatternInput(expr, input => BuildStructPropertyDesignation(pattern, input, inputType));
            }
            // A reference-type variable binds the input first, then the variable is matched
            var check = pattern.Kind == NodeKind.PatDecl
                ? BuildTypeCheck(new IlVar(name, null), inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type))
                : BuildPropertyMatch(new IlVar(name, null), pattern, inputType);
            if (check == null) return null;
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(new IlVar(name, null), expr, null));
            stats.Add(new IlReturn(check, null));
            return new IlIife(stats, null);
        }
        var sub = BuildIsSubPattern(expr, pattern, inputType);
        if (sub == null) return null;
        // A match that always succeeds (`{ }` on a value-type input, a type pattern of the same type) does not read the input, but the left side expression is evaluated (its side effects and faults are kept)
        if (sub is IlLit && !(expr is IlLit || expr is IlVar))
        {
            var stats = new List<IlStat>();
            stats.Add(new IlLocal("_", expr, null, null));
            stats.Add(new IlReturn(sub, null));
            return new IlIife(stats, null);
        }
        return new IlParen(sub, null);
    }

    // Switch expression: IIFE, pre-binding, if chain. Arm values are copy points
    private IlExpr? BuildSwitchExpression(Node sx)
    {
        var governing = BuildExpr(sx.A!);
        if (governing == null) return null;
        var inputType = NaturalType(sx.A!);
        var sw = new IlVar("__tcs_sw", null);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_sw", governing, null, null));
        if (sx.List != null)
        {
            foreach (var arm in sx.List)
            {
                if (arm.A != null && HasDesignation(arm.A)) stats.Add(SwitchDesignationLocal(arm.A, inputType, sw));
            }
        }
        var arms = new List<IlArm>();
        IlBlock? elseBlock = null;
        if (sx.List != null)
        {
            foreach (var arm in sx.List)
            {
                var value = BuildExpr(arm.C!);
                if (value == null) return null;
                value = WrapStructCopy(arm.C!, value);
                if (arm.A!.Kind == NodeKind.PatDiscard && arm.B == null)
                {
                    elseBlock = new IlBlock(One(new IlReturn(value, null)));
                    continue;
                }
                var pattern = BuildSwitchPattern(arm.A, sw, inputType);
                if (pattern == null) return null;
                if (arm.B != null)
                {
                    var when = BuildExpr(arm.B);
                    if (when == null) return null;
                    pattern = new IlBin(IlBinOp.And, pattern, when, null);
                }
                arms.Add(new IlArm(pattern, new IlBlock(One(new IlReturn(value, null)))));
            }
        }
        if (arms.Count == 0 && elseBlock == null) return null;
        // Without a discard arm, no match is a fault (il-spec §12)
        if (elseBlock == null) elseBlock = NoMatchBlock();
        if (arms.Count > 0) stats.Add(new IlIf(arms, elseBlock, null));
        else stats.Add(new IlIf(Arm(IlLiterals.Bool(true), elseBlock!), null, null));
        return new IlIife(stats, null);
    }

    // Switch statement
    private bool BuildSwitchStatInto(Node switchStmt, List<IlStat> acc)
    {
        var origin = Origin(switchStmt);
        bool needsBreakScope = SwitchNeedsBreakScope(switchStmt);
        var governing = BuildExpr(switchStmt.A!);
        if (governing == null) return false;
        var inputType = NaturalType(switchStmt.A!);
        var sw = new IlVar("__tcs_sw", null);
        var products = new List<IlStat>();
        products.Add(new IlLocal("__tcs_sw", governing, null, origin));
        Node? defaultSection = null;
        var sections = switchStmt.List ?? new List<Node>();
        foreach (var section in sections)
        {
            if (section.List == null) continue;
            foreach (var label in section.List)
            {
                if (label.Kind == NodeKind.DefaultLabel && defaultSection == null) defaultSection = section;
            }
        }
        foreach (var section in sections)
        {
            if (section.List == null) continue;
            foreach (var label in section.List)
            {
                if (label.Kind == NodeKind.CaseLabel && label.A != null && HasDesignation(label.A))
                    products.Add(SwitchDesignationLocal(label.A, inputType, sw) with { Origin = origin });
            }
        }
        var arms = new List<IlArm>();
        IlBlock? defaultBody = null;
        foreach (var section in sections)
        {
            var caseLabels = new List<Node>();
            if (section.List != null)
            {
                foreach (var l in section.List)
                {
                    if (l.Kind != NodeKind.DefaultLabel) caseLabels.Add(l);
                }
            }
            if (caseLabels.Count == 0) continue;
            IlExpr? cond = null;
            foreach (var label in caseLabels)
            {
                IlExpr? one;
                var pat = label.A!;
                bool constantLabel = label.B == null && (pat.Kind == NodeKind.PatConst || (pat.Kind == NodeKind.PatType && pat.A != null && pat.A.Kind != NodeKind.PredefType && pat.A.Kind != NodeKind.ArrayType && pat.A.Kind != NodeKind.NullableType));
                if (constantLabel && pat.Kind == NodeKind.PatType) one = BuildTypeCheck(sw, inputType, pat.Type, FormatTypeReference(pat.A!, pat.Type));
                else if (constantLabel)
                {
                    if (IsNullLiteral(pat.A!) && IlTypes.IsNullableValueType(inputType)) one = new IlBin(IlBinOp.Eq, sw, IlLiterals.Null(), null);
                    else one = BuildConstantPattern(sw, pat.A!, inputType);
                }
                else one = BuildPatternLabel(label, sw, inputType);
                if (one == null) return false;
                cond = cond == null ? one : Ex(new IlBin(IlBinOp.Or, cond, one, null));
            }
            if (cond == null) return false;
            var body = BuildSwitchSectionBody(section.List2);
            if (body == null) return false;
            if (section == defaultSection) defaultBody = body;
            arms.Add(new IlArm(cond, body));
        }
        if (defaultSection != null && defaultBody == null)
        {
            defaultBody = BuildSwitchSectionBody(defaultSection.List2);
            if (defaultBody == null) return false;
        }
        if (arms.Count > 0) products.Add(new IlIf(arms, defaultBody, origin));
        else if (defaultBody != null) products.Add(new IlDo(defaultBody, origin));
        if (needsBreakScope) acc.Add(new IlBreakScope(new IlBlock(products), origin));
        else
        {
            foreach (var p in products) acc.Add(p);
        }
        return true;
    }

    private IlExpr? BuildPatternLabel(Node label, IlExpr sw, TypeSym? inputType)
    {
        var condition = BuildSwitchPattern(label.A!, sw, inputType);
        if (condition == null) return null;
        if (label.B == null) return new IlParen(condition, null);
        var when = BuildExpr(label.B);
        return when == null ? null : new IlParen(new IlBin(IlBinOp.And, condition, when, null), null);
    }

    private IlBlock? BuildSwitchSectionBody(List<Node>? statements)
    {
        var acc = new List<IlStat>();
        if (statements == null) return new IlBlock(acc);
        var terminal = new List<Node>();
        CollectTerminalBreaks(statements, terminal);
        return BuildSectionStatsInto(statements, terminal, acc) ? new IlBlock(acc) : null;
    }

    private bool BuildSectionStatsInto(List<Node> statements, List<Node> terminal, List<IlStat> acc)
    {
        foreach (var stmt in statements)
        {
            if (terminal.Contains(stmt)) continue;
            if (stmt.Kind == NodeKind.Block)
            {
                if (!BuildSectionStatsInto(stmt.List ?? new List<Node>(), terminal, acc)) return false;
                continue;
            }
            if (!BuildStatInto(stmt, acc)) return false;
        }
        return true;
    }

    private static void CollectTerminalBreaks(List<Node> statements, List<Node> sink)
    {
        if (statements.Count == 0) return;
        var last = statements[statements.Count - 1];
        if (last.Kind == NodeKind.Break) sink.Add(last);
        else if (last.Kind == NodeKind.Block && last.List != null) CollectTerminalBreaks(last.List, sink);
    }

    // Whether it contains an early break (a break other than the implicit end, bound to this switch rather than an inner loop / switch)
    private static bool SwitchNeedsBreakScope(Node switchStmt)
    {
        var terminal = new List<Node>();
        if (switchStmt.List != null)
        {
            foreach (var section in switchStmt.List)
            {
                if (section.List2 != null) CollectTerminalBreaks(section.List2, terminal);
            }
        }
        return HasOwnBreak(switchStmt, switchStmt, terminal);
    }

    private static bool HasOwnBreak(Node n, Node sw, List<Node> terminal)
    {
        if (n != sw && (n.Kind == NodeKind.For || n.Kind == NodeKind.While || n.Kind == NodeKind.DoWhile || n.Kind == NodeKind.Foreach || n.Kind == NodeKind.Switch)) return false;
        if (n.Kind == NodeKind.Break && !terminal.Contains(n)) return true;
        return AnyChild(n, c => HasOwnBreak(c, sw, terminal));
    }

    // ---- lambda ----

    private IlExpr? BuildLambda(Node lambda)
    {
        var paramList = new List<string>();
        var copies = new List<IlStat>();
        if (lambda.List != null)
        {
            foreach (var p in lambda.List)
            {
                var name = p.Local != null ? p.Local.IlName : p.Text;
                paramList.Add(name);
                var t = p.Local != null ? p.Local.Type : null;
                var c = CopyIfMutableStruct(t, new IlVar(name, null));
                if (c is IlStructCopy sc) copies.Add(new IlAssign(sc.E, sc, null));
            }
        }
        var dt = lambda.Type;
        bool returnsVoid = dt != null && dt.Kind == TypeKind.Delegate && dt.Ret != null && dt.Ret.Kind == TypeKind.Void;
        ScopeRoots.Add(lambda);
        var outerFn = Ctx.CurrentLambdaVoid;
        Ctx.CurrentLambdaVoid = returnsVoid ? 1 : 2;
        IlExpr? result = null;
        if (lambda.A != null)
        {
            var body = BuildExpr(lambda.A);
            if (body != null)
            {
                if (!returnsVoid) body = WrapStructCopy(lambda.A, body);
                var locals = new List<string>();
                var designations = new List<Node>();
                CollectIsDesignations(lambda.A, lambda.A, designations);
                foreach (var d in designations)
                {
                    if (!locals.Contains(d.Local!.IlName)) locals.Add(d.Local.IlName);
                }
                if (copies.Count == 0) result = new IlClosure(paramList, null, body, locals, null);
                else
                {
                    var stats = new List<IlStat>();
                    foreach (var c in copies) stats.Add(c);
                    if (returnsVoid) stats.Add(new IlCallStat(body, null));
                    else stats.Add(new IlReturn(body, null));
                    result = new IlClosure(paramList, new IlBlock(stats), null, locals, null);
                }
            }
        }
        else if (lambda.B != null)
        {
            var acc = new List<IlStat>();
            foreach (var c in copies) acc.Add(c);
            if (BuildStatsInto(lambda.B.List, acc)) result = new IlClosure(paramList, new IlBlock(acc), null, new List<string>(), null);
        }
        Ctx.CurrentLambdaVoid = outerFn;
        ScopeRoots.RemoveAt(ScopeRoots.Count - 1);
        return result;
    }

    // ---- Names and members ----

    // const (except enum members) inlines the value
    public IlExpr? ConstLiteral(Symbol? sym)
    {
        if (sym == null || !sym.IsConst || sym.Kind == SymKind.EnumMember || sym.ConstVal == null) return null;
        var c = sym.ConstVal;
        // An integer value of a float const becomes a float literal
        if (c.Kind == ConstKind.Int && (sym.Type.Kind == TypeKind.Float || sym.Type.Kind == TypeKind.Double)) return IlLiterals.Float((float)c.I);
        return IlLiterals.Of(c);
    }

    private IlExpr? BuildIdentifier(Node id)
    {
        var sym = id.Sym;
        var constLit = ConstLiteral(sym);
        if (constLit != null) return constLit;
        switch (id.Info)
        {
            case RefKind.Local:
                return new IlVar(id.Local!.IlName, null);
            case RefKind.MethodGroup:
                {
                    var m = id.Sym;
                    if (m != null && m.IsStatic && m.Owner != null) return MethodGroup(m, id.TypeArgs);
                    return null;
                }
            case RefKind.Field:
            case RefKind.Property:
                {
                    var s = sym!;
                    if (s.Kind == SymKind.Property && IsCustomProperty(s))
                    {
                        if (s.IsStatic) return new IlDynCall(new IlField(new IlVar(TypeRef(s.Owner!), null), "get_" + N(s), null), StaticAccessorArgs(s, null), null);
                        if (IlTypes.IsUserStruct(s.Owner!.SelfType())) return new IlCall(TypeRef(s.Owner) + ".get_" + N(s), One(new IlVar("self", null)), null);
                        return new IlInvoke(new IlVar("self", null), "get_" + N(s), new List<IlExpr>(), null);
                    }
                    if (!s.IsStatic) return new IlField(new IlVar("self", null), N(s), null);
                    return new IlField(new IlVar(TypeRef(s.Owner!), null), N(s), null);
                }
            case RefKind.EnumMember:
                return EnumMemberIl(sym!);
            case RefKind.Type:
                if (id.Type != null && id.Type.Decl != null) return new IlVar(TypeRef(id.Type.Decl), null);
                return Unported("type reference", id);
        }
        return Unported("identifier " + id.Info, id);
    }

    // Enum members are fields of the type's table (a --ref enum uses the host path and UPPER_SNAKE names)
    private IlExpr EnumMemberIl(Symbol sym)
    {
        // A --ref enum that belongs to neither a namespace nor a type uses a path of the enum's own name
        if (sym.Owner != null && sym.Owner.IsRef && sym.Owner.IlName == "") return new IlField(new IlVar(HostNames.HostPathSegment(sym.Owner.Name), null), N(sym), null);
        return new IlField(new IlVar(TypeRef(sym.Owner!), null), N(sym), null);
    }

    private IlExpr? BuildMemberAccess(Node ma)
    {
        if (ma.Value == 1 && ma != _condBinding) return Unported("conditional access", ma);
        var sym = ma.Sym;
        if (ma.Info == RefKind.Type)
        {
            if (ma.Type != null && ma.Type.Decl != null) return new IlVar(TypeRef(ma.Type.Decl), null);
            return Unported("type member", ma);
        }
        if (ma.Info == RefKind.Namespace) return new IlVar(ma.Text, null);
        if (sym == null) return Unported("member without symbol", ma);
        var constLit = ConstLiteral(sym);
        if (constLit != null) return constLit;
        if (sym.Kind == SymKind.EnumMember) return EnumMemberIl(sym);
        if (sym.Builtin != "") return BuildBuiltinMember(ma, sym);
        if (ma.Info == RefKind.MethodGroup)
        {
            if (sym.IsStatic && sym.Owner != null) return MethodGroup(sym, ma.TypeArgs);
            return null;
        }
        if (sym.Kind == SymKind.Property)
        {
            var obj = sym.IsStatic ? Ex(new IlVar(TypeRef(sym.Owner!), null)) : BuildExpr(ma.A!);
            if (obj == null) return null;
            if (IsCustomProperty(sym))
            {
                if (sym.IsStatic) return new IlDynCall(new IlField(new IlVar(TypeRef(sym.Owner!), null), "get_" + N(sym), null), StaticAccessorArgs(sym, ma.A), null);
                if (IlTypes.IsUserStruct(ma.A!.Type)) return new IlCall(TypeRef(sym.Owner!) + ".get_" + N(sym), One(StructReceiverArg(ma.A, obj)), null);
                return new IlInvoke(obj, "get_" + N(sym), new List<IlExpr>(), null);
            }
            return new IlField(obj, N(sym), null);
        }
        if (sym.Kind == SymKind.Field)
        {
            var obj = sym.IsStatic ? Ex(new IlVar(TypeRef(sym.Owner!), null)) : BuildExpr(ma.A!);
            return obj == null ? null : new IlField(obj, N(sym), null);
        }
        return Unported("member", ma);
    }

    // Values of built-in members (Count, Length, HasValue, etc.)
    private IlExpr? BuildBuiltinMember(Node ma, Symbol sym)
    {
        var id = sym.Builtin;
        var recvType = ma.A != null ? ma.A.Type : null;
        if (IsFacadeReceiver(ma.A)) return new IlField(new IlVar(ma.A!.Type!.Name, null), ma.Text, null);
        switch (id)
        {
            // System.Math consts inline the double value
            case "Math.PI": return IlLiterals.Float(3.141592653589793f);
            case "Math.E": return IlLiterals.Float(2.718281828459045f);
            case "String.Empty": return new IlField(new IlVar("String", null), "Empty", null);
            case "Random.Shared": return new IlField(new IlVar("TinySystem.Random", null), "Shared", null);
            case "Float.MaxValue": return IlLiterals.Float(float.MaxValue);
            case "Float.MinValue": return IlLiterals.Float(float.MinValue);
            case "Float.PositiveInfinity": return IlLiterals.Float(float.PositiveInfinity);
            case "Float.NegativeInfinity": return IlLiterals.Float(float.NegativeInfinity);
            case "Float.NaN": return IlLiterals.Float(float.NaN);
            case "Float.Epsilon": return IlLiterals.Float(float.Epsilon);
        }
        var obj = BuildExpr(ma.A!);
        if (obj == null) return null;
        switch (id)
        {
            case "Nullable.HasValue": return new IlNullableHasValue(obj, null);
            case "Nullable.Value": return new IlNullableValue(obj, null);
            case "Queue.Count": return new IlCall("TcsQueue.Count", One(obj), null);
            case "HashSet.Count": return new IlCall("TcsHashSet.Count", One(obj), null);
            case "Dict.Count": return new IlCall("Dict.Count", One(obj), null);
            case "List.Count": return new IlLen(obj, null);
            case "Dict.Keys": return new IlCall("Dict.Keys", One(obj), null);
            case "Dict.Values": return new IlCall("Dict.Values", One(obj), null);
            case "Array.Length": return new IlLen(obj, null);
            case "Rune.Value": return obj;
            case "Utf8.Length": return new IlField(obj, "Length", null);
            case "KVP.Key": return new IlField(obj, "Key", null);
            case "KVP.Value": return new IlField(obj, "Value", null);
        }
        if (IlTypes.IsDictCollection(recvType) && sym.Name == "Count") return new IlLen(obj, null);
        return Unported("builtin member " + id, ma);
    }

    // Auto-implemented property (none of the accessors has a body)
    public static bool IsAutoProperty(Node prop)
    {
        if (prop.B == null && prop.C == null) return false;
        if (prop.B != null && (prop.B.A != null || prop.B.B != null)) return false;
        if (prop.C != null && (prop.C.A != null || prop.C.B != null)) return false;
        // An expression-bodied property (`int P => e;`) is a get with the expression in B
        return true;
    }

    // Whether there is an accessor with a body / expression body (false for auto-implemented and built-in properties)
    public static bool IsCustomProperty(Symbol p)
    {
        if (p.Kind != SymKind.Property) return false;
        if (p.Owner != null && p.Owner.IsInterface) return true;
        var d = p.Decl;
        if (d == null || d.Kind != NodeKind.Property) return false;
        return !IsAutoProperty(d);
    }

    // Default values of the type arguments passed to an accessor of a static property of a generic type
    // Default values of the type arguments passed to an accessor of a static property of a generic type (the type arguments of the receiver type; a reference inside the type uses its own type arguments)
    private List<IlExpr> StaticAccessorArgs(Symbol p, Node? recv)
    {
        var r = new List<IlExpr>();
        if (p.Owner == null || !IlTypes.PassesTypeArgDefaults(p.Owner)) return r;
        List<TypeSym>? args = null;
        if (recv != null && recv.Type != null && recv.Type.Decl == p.Owner && recv.Type.Args != null) args = recv.Type.Args;
        if (args == null) args = p.Owner.SelfType().Args;
        foreach (var a in args!) r.Add(DefaultIl(a));
        return r;
    }

    // ---- Names ----

    // Whether it is a receiver of a static TinySystem facade (`TinySystem.Math`, `Math` with only `using TinySystem;`)
    private bool IsFacadeReceiver(Node? recv)
    {
        if (recv == null || recv.Type == null) return false;
        if (recv.Type.Kind != TypeKind.StaticRef) return false;
        var n = recv.Type.Name;
        return n == "TinySystem.Math" || n == "TinySystem.String" || n == "TinySystem.List" || n == "TinySystem.Dict";
    }

    // using flags: 1 = TinySystem, 2 = System
    private static int CollectUsings(Node container)
    {
        int r = 0;
        if (container.List == null) return r;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using)
            {
                if (n.Text == "TinySystem") r = r | 1;
                if (n.Text == "System") r = r | 2;
            }
            else if (n.Kind == NodeKind.Namespace) r = r | CollectUsings(n);
        }
        return r;
    }

    // The IL member name of symbol. Members of --ref types use the host name (IlName); methods of user types use the overload name
    public string N(Symbol sym)
    {
        if (sym.Owner != null && sym.Owner.IsRef) return sym.IlName;
        if (sym.Kind == SymKind.Method) return IlNames.MethodName(sym);
        return sym.Name;
    }

    // The type name of a static reference (the host path for --ref types)
    public string TypeRef(TypeDeclSym t)
    {
        return t.IlName;
    }

    // The type name for type checks (IlIsType) and casts (IlCast). For --ref types, the C# global name (`Ed_Transform`, the same as the exported class name) rather than the host name (`ed.transform`)
    private string CheckTypeName(TypeSym t)
    {
        if (t.Decl != null && t.Decl.IsRef) return t.Decl.FullName.Replace(".", "_");
        return TypeName(t);
    }

    public string TypeName(TypeSym t)
    {
        if (t.Kind == TypeKind.Tuple) return "System_ValueTuple" + t.Args!.Count;
        if (t.Decl != null) return t.Decl.IlName;
        return t.Name;
    }

    // ---- method group ----

    // Struct parameters are passed by value. The runtime (LINQ / Sort, etc.) passes elements without copying, so the receiving side copies (il-spec §10)
    private IlExpr MethodGroup(Symbol method, List<TypeSym>? typeArgs)
    {
        var typeArgDefaults = new List<IlExpr>();
        if ((method.IsStatic) && IlTypes.PassesTypeArgDefaults(method.Owner))
        {
            foreach (var a in method.Owner!.SelfType().Args!) typeArgDefaults.Add(TypeArgDefaultValue(a));
        }
        if (method.TypeParams != null && typeArgs != null)
        {
            foreach (var a in typeArgs) typeArgDefaults.Add(TypeArgDefaultValue(a));
        }
        if (typeArgDefaults.Count == 0) return new IlMethodGroup(TypeRef(method.Owner!), N(method), null);
        var parameters = new List<string>();
        var args = new List<IlExpr>();
        int n = method.ParamCount;
        for (int i = 0; i < n; i++)
        {
            var pn = "__tcs_a" + i;
            parameters.Add(pn);
            var param = method.Params![i];
            args.Add(!param.IsOut && IlTypes.IsMutableStruct(param.Type) ? CopyIfMutableStruct(param.Type, new IlVar(pn, null)) : Ex(new IlVar(pn, null)));
        }
        foreach (var t in typeArgDefaults) args.Add(t);
        IlExpr groupCall = new IlCall(TypeRef(method.Owner!) + "." + N(method), args, null);
        return new IlClosure(parameters, null, groupCall, new List<string>(), null);
    }

    // ---- Binary operations ----

    private IlExpr? BuildBinary(Node bin)
    {
        var op = bin.Text;
        if (op == "is" || op == "as") return Unported("is / as", bin);
        // Skip the int → float conversion of float arithmetic operands (the VM and C operations promote int to float)
        bool arithmetic = op == "+" || op == "-" || op == "*" || op == "/" || op == "%";
        bool builtinOp = bin.Sym == null || bin.Sym.Builtin != "";
        if (arithmetic && builtinOp)
        {
            if (bin.A!.Kind == NodeKind.Conv) _promotedOperand = bin.A;
        }
        var left = BuildExpr(bin.A!);
        if (arithmetic && builtinOp && bin.B!.Kind == NodeKind.Conv) _promotedOperand = bin.B;
        var right = BuildExpr(bin.B!);
        _promotedOperand = null;
        if (left == null || right == null) return null;
        if (WritesLocalReadBy(bin.A!, bin.B!)) return SnapshotOperand(left, l => BuildBinaryOperands(bin, l, right));
        return BuildBinaryOperands(bin, left, right);
    }

    // The IIFE `local __tcs_l = first; return build(__tcs_l)`
    private static IlExpr? SnapshotOperand(IlExpr first, System.Func<IlExpr, IlExpr?> build)
    {
        var applied = build(new IlVar("__tcs_l", null));
        if (applied == null) return null;
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_l", first, null, null));
        stats.Add(new IlReturn(applied, null));
        return new IlIife(stats, null);
    }

    // Whether evaluating later may modify a local / parameter that earlier reads: a direct write by assignment / ++ / -- / ref or out argument,
    // or when earlier reads a (captured) local written inside a lambda and later may run user code (a call, a construction, a custom property getter, a user operator)
    private bool WritesLocalReadBy(Node earlier, Node later)
    {
        var read = new List<LocalSym>();
        CollectLocalReads(earlier, read);
        if (read.Count == 0) return false;
        if (WritesAny(later, read)) return true;
        if (!MayWriteStorage(later)) return false;
        var member = ScopeRoots.Count > 0 ? ScopeRoots[0] : null;
        if (member == null) return false;
        foreach (var l in read)
        {
            var one = new List<LocalSym>();
            one.Add(l);
            if (LambdaWrites(member, one)) return true;
        }
        return false;
    }

    private static void CollectLocalReads(Node n, List<LocalSym> into)
    {
        if (n.Kind == NodeKind.Name && n.Info == RefKind.Local && n.Local != null && !into.Contains(n.Local)) into.Add(n.Local);
        VisitChildren(n, c => CollectLocalReads(c, into));
    }

    private static bool LambdaWrites(Node n, List<LocalSym> symbols)
    {
        if (n.Kind == NodeKind.Lambda && WritesAny(n, symbols)) return true;
        return AnyChild(n, c => LambdaWrites(c, symbols));
    }

    private static bool WritesAny(Node n, List<LocalSym> symbols)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
                if (Targets(n.A!, symbols)) return true;
                break;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (Targets(n.A!, symbols)) return true;
                break;
            case NodeKind.Arg:
                if ((n.Has(Mods.Ref) || n.Has(Mods.Out) || n.Has(Mods.In)) && n.A != null && Targets(n.A, symbols)) return true;
                break;
        }
        return AnyChild(n, c => WritesAny(c, symbols));
    }

    private static bool Targets(Node target, List<LocalSym> symbols)
    {
        var t = StripConv(target);
        if (t.Kind == NodeKind.Name && t.Local != null) return symbols.Contains(t.Local);
        if (t.Kind == NodeKind.Paren) return Targets(t.A!, symbols);
        if (t.Kind == NodeKind.Tuple && t.List != null)
        {
            foreach (var a in t.List)
            {
                var x = a.Kind == NodeKind.Arg ? a.A! : a;
                if (Targets(x, symbols)) return true;
            }
        }
        return false;
    }

    // Combinations where == is a reference comparison and yet the runtime value may be a record (if one side has a record static type, it is record equality).
    // Comparison with a null literal is excluded because value equality and reference comparison give the same result
    private static bool IsReferenceEquality(TypeSym lt, TypeSym rt)
    {
        if (lt.Kind == TypeKind.Null || rt.Kind == TypeKind.Null) return false;
        if (IsRecordClass(lt) || IsRecordClass(rt)) return false;
        return MayHoldRecord(lt) && MayHoldRecord(rt);
    }

    private static bool IsRecordClass(TypeSym t) => t.Kind == TypeKind.Class && t.Decl != null && t.Decl.IsRecord;

    private static bool MayHoldRecord(TypeSym t) =>
        t.Kind == TypeKind.Object || t.Kind == TypeKind.Interface || t.Kind == TypeKind.TypeParam;

    private IlExpr? BuildBinaryOperands(Node bin, IlExpr left, IlExpr right)
    {
        var op = bin.Text;
        // A user operator is a direct call of the statically chosen overload
        if (bin.Sym != null && bin.Sym.Builtin == "")
        {
            var operands = new List<Node>();
            operands.Add(bin.A!);
            operands.Add(bin.B!);
            var built = new List<IlExpr>();
            built.Add(left);
            built.Add(right);
            var userOp = TryBuildUserOperatorCall(bin, operands, built);
            if (userOp != null) return userOp;
            // Operators of --ref types are implemented in the host, so they stay IL operations instead of calls
            if (!(bin.Sym.Owner != null && bin.Sym.Owner.IsRef)) return Unported("user operator", bin);
        }
        var lt = bin.A!.Type!;
        var rt = bin.B!.Type!;
        var resultType = bin.Type!;
        // ==/!= on a record struct goes to the synthesized value equality
        if ((op == "==" || op == "!=") && lt.Decl != null && lt.Decl.IsRecord && IlTypes.IsUserStruct(lt))
        {
            var eqCall = new IlCall(TypeName(lt) + ".op_Equality", Two(left, right), null);
            if (op == "==") return eqCall;
            return new IlParen(new IlUn(IlUnOp.Not, eqCall, null), null);
        }
        // ==/!= between object / interface / type arguments is a reference comparison in C#, so it becomes __tcs_refeq instead of record value equality
        if ((op == "==" || op == "!=") && IsReferenceEquality(lt, rt))
        {
            var refEq = new IlCall("__tcs_refeq", Two(left, right), null);
            if (op == "==") return refEq;
            return new IlParen(new IlUn(IlUnOp.Not, refEq, null), null);
        }
        var nullableBin = TryBuildNullableBinary(bin, left, right);
        if (nullableBin != null) return nullableBin;
        if (op == "/" && IlTypes.IsIntegral(resultType)) return new IlCall("__tcs_idiv", Two(left, right), null);
        if (op == "%")
        {
            if (IlTypes.IsIntegral(resultType)) return new IlCall("__tcs_irem", Two(left, right), null);
            if (IlTypes.IsFloating(resultType)) return new IlCall("__tcs_fmod", Two(left, right), null);
        }
        bool isConcat = op == "+" && (lt.Kind == TypeKind.String || rt.Kind == TypeKind.String || resultType.Kind == TypeKind.String);
        if (isConcat) return new IlBin(IlBinOp.Concat, WrapConcatOperand(bin.A, left), WrapConcatOperand(bin.B, right), null);
        if (_checked && resultType.Kind == TypeKind.Int && (op == "+" || op == "-" || op == "*"))
        {
            var callee = op == "+" ? "__tcs_cadd" : op == "-" ? "__tcs_csub" : "__tcs_cmul";
            return new IlCall(callee, Two(left, right), null);
        }
        bool boolOperand = lt.Kind == TypeKind.Bool || rt.Kind == TypeKind.Bool;
        bool enumOperand = lt.Kind == TypeKind.Enum || rt.Kind == TypeKind.Enum;
        // The ordering of a uint enum is unsigned
        bool unsignedRelation = IlTypes.IsUIntEnum(lt) || IlTypes.IsUIntEnum(rt);
        switch (op)
        {
            // + / - of an enum and an integer, and - between enums, wrap into the range of the result type (the enum, or the underlying type for - between enums)
            case "+": return enumOperand ? WrapToResult(resultType, new IlBin(IlBinOp.AddNum, left, right, null)) : new IlBin(IlBinOp.AddNum, left, right, null);
            case "-": return enumOperand ? WrapToResult(resultType, new IlBin(IlBinOp.Sub, left, right, null)) : new IlBin(IlBinOp.Sub, left, right, null);
            case "*": return new IlBin(IlBinOp.Mul, left, right, null);
            case "/": return new IlBin(IlBinOp.DivNum, left, right, null);
            case "%": return new IlBin(IlBinOp.RemNum, left, right, null);
            case "==": return new IlBin(IlBinOp.Eq, left, right, null);
            case "!=": return new IlBin(IlBinOp.Ne, left, right, null);
            case "<": return Relation(IlBinOp.Lt, left, right, unsignedRelation);
            case "<=": return Relation(IlBinOp.Le, left, right, unsignedRelation);
            case ">": return Relation(IlBinOp.Gt, left, right, unsignedRelation);
            case ">=": return Relation(IlBinOp.Ge, left, right, unsignedRelation);
            case "&&": return new IlBin(IlBinOp.And, left, right, null);
            case "||": return new IlBin(IlBinOp.Or, left, right, null);
            case "??": return new IlBin(IlBinOp.Or, left, right, null);
            case "&":
                if (!boolOperand) return new IlBin(IlBinOp.BitAnd, left, right, null);
                break;
            case "|":
                if (!boolOperand) return new IlBin(IlBinOp.BitOr, left, right, null);
                break;
            case "^":
                if (!boolOperand) return new IlBin(IlBinOp.BitXor, left, right, null);
                break;
            case "<<": return new IlBin(IlBinOp.Shl, left, right, null);
            case ">>": return new IlBin(IlBinOp.Shr, left, right, null);
        }
        return null;
    }

    private static IlExpr WrapToResult(TypeSym resultType, IlExpr value)
    {
        if (resultType.Kind == TypeKind.Enum) return WrapToEnum(resultType, value);
        return resultType.IsIntegral ? WrapToInteger(resultType, value, false) : value;
    }

    // ---- Stringification ----

    // The natural type before conversion (Roslyn's GetTypeInfo(expr).Type). The type of the expression inside the Binder's Conv
    public static TypeSym NaturalType(Node e)
    {
        while (e.Kind == NodeKind.Conv && e.A != null) e = e.A;
        return e.Type ?? TypeSym.ErrorType;
    }

    public static Node StripConv(Node e)
    {
        while (e.Kind == NodeKind.Conv && e.A != null) e = e.A;
        return e;
    }

    // Stringification (interpolation / ToString)
    private IlExpr? WrapFloatToString(Node src, IlExpr built)
    {
        var t = NaturalType(src);
        var user = UserToString(src, built);
        if (user != null) return user;
        var en = WrapEnumToString(t, built);
        if (en != null) return en;
        if (IsStringifiedByValue(t)) return StringifyByValue(t, built);
        if (IlTypes.IsFloating(t)) return new IlCall("__tcs_fstr", One(built), null);
        if (IlTypes.IsChar(t)) return new IlCall("__tcs_cstr", One(built), null);
        if (IlTypes.IsRune(t)) return RuneToString(built);
        if (t.Kind == TypeKind.Bool) return new IlCall("__tcs_bstr", One(built), null);
        return new IlCall("__tcs_str", One(built), null);
    }

    // Converts operands of string concatenation (`+` / `+=`) to strings: value types other than int and ToString overrides are stringified here,
    // and a string that may be null becomes an empty string (int is converted to decimal by the concatenation (IlBinOp.Concat))
    private IlExpr WrapConcatOperand(Node expr, IlExpr rendered)
    {
        var type = NaturalType(expr);
        var user = UserToString(expr, rendered);
        if (user != null) return user;
        var en = WrapEnumToString(type, rendered);
        if (en != null) return en;
        if (IsStringifiedByValue(type)) return StringifyByValue(type, rendered);
        if (IlTypes.IsFloating(type)) return new IlCall("__tcs_fstr", One(rendered), null);
        if (IlTypes.IsChar(type)) return new IlCall("__tcs_cstr", One(rendered), null);
        if (IlTypes.IsRune(type)) return RuneToString(rendered);
        if (IlTypes.UnwrapNullable(type).Kind == TypeKind.Bool)
        {
            if (IlTypes.IsNullableValueType(type)) return new IlCall("__tcs_nstr", One(rendered), null);
            return new IlCall("__tcs_bstr", One(rendered), null);
        }
        if (type.Kind != TypeKind.String) return rendered;
        var unwrapped = expr;
        while (unwrapped.Kind == NodeKind.Paren || (unwrapped.Kind == NodeKind.Conv && unwrapped.Info == ConvKind.Identity)) unwrapped = unwrapped.A!;
        bool literalLike = unwrapped.Kind == NodeKind.StringLit || unwrapped.Kind == NodeKind.InterpString
            || (unwrapped.Kind == NodeKind.Binary && unwrapped.Text == "+") || (unwrapped.HasConst && unwrapped.Kind != NodeKind.Name && unwrapped.Kind != NodeKind.Member);
        if (literalLike) return rendered;
        return new IlParen(new IlBin(IlBinOp.Or, rendered, IlLiterals.Str(""), null), null);
    }

    // Stringification of a value whose type overrides ToString: calls it directly (null, and a null returned by ToString, become an empty string)
    private IlExpr? UserToString(Node src, IlExpr built)
    {
        var t = NaturalType(src);
        if (t == null || t.Decl == null || t.Decl.IsRef) return null;
        Symbol? method = null;
        var cands = t.Decl.Lookup("ToString");
        if (cands != null)
        {
            foreach (var c in cands)
            {
                if (c.Kind == SymKind.Method && c.ParamCount == 0 && !c.IsStatic) method = c;
            }
        }
        if (method == null) return null;
        if (IlTypes.IsUserStruct(t))
            return EmptyIfNull(new IlCall(TypeRef(t.Decl) + "." + N(method), One(WrapStructCopy(src, built)), null));
        var value = new IlVar("__tcs_v", null);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_v", built, null, null));
        stats.Add(new IlReturn(EmptyIfNull(new IlTernary(new IlBin(IlBinOp.Ne, value, IlLiterals.Null(), null),
            new IlInvoke(value, N(method), new List<IlExpr>(), null), IlLiterals.Str(""), null)), null));
        return new IlIife(stats, null);
    }

    private static IlExpr EmptyIfNull(IlExpr text)
    {
        return new IlParen(new IlBin(IlBinOp.Or, text, IlLiterals.Str(""), null), null);
    }

    // A Rune value is the codepoint integer itself
    private static IlExpr RuneToString(IlExpr rune)
    {
        return new IlCall("Char.ConvertFromUtf32", One(rune), null);
    }

    // Types whose stringification depends on the value (T?, object, type arguments)
    private static bool IsStringifiedByValue(TypeSym t)
    {
        return t.Kind == TypeKind.Nullable || t.Kind == TypeKind.Object || t.Kind == TypeKind.TypeParam;
    }

    private IlExpr StringifyByValue(TypeSym t, IlExpr value)
    {
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_tstr", Two(passed, value), null);
        }
        return new IlCall("__tcs_nstr", One(value), null);
    }

    // Stringification of enum values (ToString / concatenation / interpolation / Console.WriteLine). As in .NET's Enum.ToString, a defined value is the member name,
    // [Flags] (and format "F") join names with ", ", and a value that neither can express is a number. The name table is placed where the stringification happens (the second argument of String.FormatEnum)
    private IlExpr? WrapEnumToString(TypeSym t, IlExpr value)
    {
        return WrapEnumToStringFlags(t, value, false);
    }

    private static IlExpr? WrapEnumToStringFlags(TypeSym t, IlExpr value, bool flags)
    {
        var spec = EnumFormatSpec(IlTypes.UnwrapNullable(t), flags);
        if (spec == null) return null;
        return new IlCall("String.FormatEnum", Two(value, IlLiterals.Str(spec)), null);
    }

    // Enum formats (`e.ToString("G")` and `{e:G}` in interpolation). G is the name, F is a combination of names, D is the number (String.FormatEnum without a name table).
    // null for other formats and for non-enum types
    private static IlExpr? WrapEnumFormat(TypeSym t, IlExpr value, string format)
    {
        if (format == "G" || format == "g") return WrapEnumToStringFlags(t, value, false);
        if (format == "F" || format == "f") return WrapEnumToStringFlags(t, value, true);
        if (format == "D" || format == "d")
        {
            var prefix = EnumSpecPrefix(IlTypes.UnwrapNullable(t));
            return prefix == null ? null : new IlCall("String.FormatEnum", Two(value, IlLiterals.Str(prefix)), null);
        }
        return null;
    }

    // The mark placed before the name table of spec. A uint underlying type is `+` (a value not in the table is written as an unsigned number). null for a type that cannot make a spec
    private static string? EnumSpecPrefix(TypeSym t)
    {
        if (t.Kind != TypeKind.Enum) return null;
        var b = IlTypes.EnumUnderlying(t);
        if (b == null) return null;
        if (b.Kind == TypeKind.UInt) return "+";
        if (b.Kind == TypeKind.Byte || b.Kind == TypeKind.Short || b.Kind == TypeKind.Int) return "";
        return null;
    }

    // spec is a list of `name:value` joined with `,`. Flags (and format "F") are prefixed with `*` and hold all members in the same order as .NET's EnumInfo
    // (ascending unsigned value; kept as is if already sorted in declaration order, sorted otherwise). Otherwise it holds only the one name .NET picks for each value
    // (for members with the same value, the first one in that order if there are 32 or fewer, otherwise the one binary search hits). A uint underlying type puts `+` before the name table
    public static string? EnumFormatSpec(TypeSym t, bool forceFlags)
    {
        var prefix = EnumSpecPrefix(t);
        if (prefix == null) return null;
        var d = t.Decl!;
        int bits = IlTypes.IntegerBits(IlTypes.EnumUnderlying(t)!);
        var names = new List<string>();
        var values = new List<int>();
        foreach (var m in d.Members)
        {
            if (m.Kind != SymKind.EnumMember) continue;
            names.Add(m.Name);
            values.Add(m.ConstVal != null ? m.ConstVal.I : 0);
        }
        var order = new List<int>();
        for (int i = 0; i < values.Count; i++) order.Add(i);
        bool sorted = true;
        for (int i = 0; i + 1 < values.Count; i++)
        {
            if (EnumKeyLess(values[i + 1], values[i], bits)) sorted = false;
        }
        if (!sorted)
        {
            // Same ordering as .NET's Array.Sort(keys, items) (introsort; 16 or fewer use insertion sort, which keeps the order of equal values,
            // but more than that does not). The key is the value, the item is the declaration order
            var keys = new List<int>();
            foreach (var v in values) keys.Add(v);
            EnumIntroSort(keys, order, 0, keys.Count, 2 * (Log2(keys.Count) + 1), bits);
        }
        var entries = new List<string>();
        if (forceFlags || d.IsFlags)
        {
            foreach (var i in order) entries.Add(names[i] + ":" + values[i]);
            return "*" + prefix + string.Join(",", entries);
        }
        int k = 0;
        while (k < order.Count)
        {
            int key = values[order[k]];
            int at;
            if (order.Count <= 32) at = k;
            else
            {
                // .NET's binary search (which of the equal values it hits is the same too)
                int lo = 0;
                int hi = order.Count - 1;
                at = k;
                while (lo <= hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    int v = values[order[mid]];
                    if (EnumKeyEqual(v, key, bits))
                    {
                        at = mid;
                        break;
                    }
                    if (EnumKeyLess(v, key, bits)) lo = mid + 1;
                    else hi = mid - 1;
                }
            }
            entries.Add(names[order[at]] + ":" + values[order[at]]);
            // Skip equal values
            int next = k + 1;
            while (next < order.Count && EnumKeyEqual(values[order[next]], key, bits)) next++;
            k = next;
        }
        return prefix + string.Join(",", entries);
    }

    private static int Log2(int n)
    {
        int r = 0;
        while (n > 1)
        {
            n = n / 2;
            r++;
        }
        return r;
    }

    // Sorts keys[lo..lo+len) and items with the same steps as .NET's ArraySortHelper<TKey, TValue>.IntroSort
    private static void EnumIntroSort(List<int> keys, List<int> items, int lo, int len, int depthLimit, int bits)
    {
        int size = len;
        while (size > 1)
        {
            if (size <= 16)
            {
                if (size == 2)
                {
                    EnumSwapIfGreater(keys, items, lo, lo + 1, bits);
                    return;
                }
                if (size == 3)
                {
                    EnumSwapIfGreater(keys, items, lo, lo + 1, bits);
                    EnumSwapIfGreater(keys, items, lo, lo + 2, bits);
                    EnumSwapIfGreater(keys, items, lo + 1, lo + 2, bits);
                    return;
                }
                EnumInsertionSort(keys, items, lo, size, bits);
                return;
            }
            if (depthLimit == 0)
            {
                EnumHeapSort(keys, items, lo, size, bits);
                return;
            }
            depthLimit--;
            int p = EnumPartition(keys, items, lo, size, bits);
            EnumIntroSort(keys, items, lo + p + 1, size - (p + 1), depthLimit, bits);
            size = p;
        }
    }

    private static int EnumPartition(List<int> keys, List<int> items, int lo, int len, int bits)
    {
        int hi = len - 1;
        int middle = hi >> 1;
        EnumSwapIfGreater(keys, items, lo, lo + middle, bits);
        EnumSwapIfGreater(keys, items, lo, lo + hi, bits);
        EnumSwapIfGreater(keys, items, lo + middle, lo + hi, bits);
        int pivot = keys[lo + middle];
        EnumSwap(keys, items, lo + middle, lo + hi - 1);
        int left = 0;
        int right = hi - 1;
        while (left < right)
        {
            left++;
            while (EnumKeyLess(keys[lo + left], pivot, bits)) left++;
            right--;
            while (EnumKeyLess(pivot, keys[lo + right], bits)) right--;
            if (left >= right) break;
            EnumSwap(keys, items, lo + left, lo + right);
        }
        if (left != hi - 1) EnumSwap(keys, items, lo + left, lo + hi - 1);
        return left;
    }

    private static void EnumInsertionSort(List<int> keys, List<int> items, int lo, int len, int bits)
    {
        for (int i = 0; i < len - 1; i++)
        {
            int t = keys[lo + i + 1];
            int tv = items[lo + i + 1];
            int j = i;
            while (j >= 0 && EnumKeyLess(t, keys[lo + j], bits))
            {
                keys[lo + j + 1] = keys[lo + j];
                items[lo + j + 1] = items[lo + j];
                j--;
            }
            keys[lo + j + 1] = t;
            items[lo + j + 1] = tv;
        }
    }

    private static void EnumHeapSort(List<int> keys, List<int> items, int lo, int n, int bits)
    {
        for (int i = n >> 1; i >= 1; i--) EnumDownHeap(keys, items, lo, i, n, bits);
        for (int i = n; i > 1; i--)
        {
            EnumSwap(keys, items, lo, lo + i - 1);
            EnumDownHeap(keys, items, lo, 1, i - 1, bits);
        }
    }

    private static void EnumDownHeap(List<int> keys, List<int> items, int lo, int i, int n, int bits)
    {
        int d = keys[lo + i - 1];
        int dv = items[lo + i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && EnumKeyLess(keys[lo + child - 1], keys[lo + child], bits)) child++;
            if (!EnumKeyLess(d, keys[lo + child - 1], bits)) break;
            keys[lo + i - 1] = keys[lo + child - 1];
            items[lo + i - 1] = items[lo + child - 1];
            i = child;
        }
        keys[lo + i - 1] = d;
        items[lo + i - 1] = dv;
    }

    private static void EnumSwapIfGreater(List<int> keys, List<int> items, int i, int j, int bits)
    {
        if (i != j && EnumKeyLess(keys[j], keys[i], bits)) EnumSwap(keys, items, i, j);
    }

    private static void EnumSwap(List<int> keys, List<int> items, int i, int j)
    {
        int k = keys[i];
        keys[i] = keys[j];
        keys[j] = k;
        int v = items[i];
        items[i] = items[j];
        items[j] = v;
    }

    // Unsigned comparison at the width of the underlying type
    private static bool EnumKeyLess(int a, int b, int bits)
    {
        if (bits < 32)
        {
            int mask = (1 << bits) - 1;
            return (a & mask) < (b & mask);
        }
        return (a ^ int.MinValue) < (b ^ int.MinValue);
    }

    private static bool EnumKeyEqual(int a, int b, int bits)
    {
        if (bits < 32)
        {
            int mask = (1 << bits) - 1;
            return (a & mask) == (b & mask);
        }
        return a == b;
    }

    // ---- Default values of type arguments ----

    private static string TypeArgDefaultName(TypeSym p)
    {
        return (p.IsMethodParam ? "__tcs_dm_" : "__tcs_dc_") + p.Name;
    }

    // IL for the default value of a type argument (a hidden argument for a method type argument; self or a hidden argument for a type's type argument)
    public IlExpr? TypeArgDefaultIl(TypeSym p)
    {
        var name = TypeArgDefaultName(p);
        if (p.IsMethodParam) return new IlVar(name, null);
        var decl = Ctx.CurrentDecl;
        if (decl == null || decl.TypeParams.Count == 0) return null;
        if (decl.DeclKind == DeclKinds.Interface || decl.DeclKind == DeclKinds.Enum) return null;
        return Ctx.TypeArgDefaultsOnSelf ? new IlField(new IlVar("self", null), name, null) : new IlVar(name, null);
    }

    // IL for a default value: an IlNewObj of the zero value for a source struct, the default passed by the caller for a type argument, a literal otherwise
    public IlExpr DefaultIl(TypeSym? t)
    {
        if (t != null && t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_dv", One(passed), null);
        }
        if (IlTypes.IsUtf8Bytes(t)) return new IlCall("TinySystem.Utf8.Default", new List<IlExpr>(), null);
        if (IlTypes.IsUserStruct(t)) return new IlNewObj(TypeName(t!), StructTypeArgDefaults(t!), "new", null);
        return DefaultLiteral(t);
    }

    // Default values that can be written as a literal (number, bool, enum; null otherwise). Source structs, Utf8Bytes, and passed type-argument defaults are handled first by DefaultIl
    public static IlLit DefaultLiteral(TypeSym? type)
    {
        if (type == null) return IlLiterals.Null();
        // The default of an enum is 0 regardless of member values (C#: default(E) == 0)
        if (type.Kind == TypeKind.Enum) return IlLiterals.Int(0);
        switch (type.Kind)
        {
            case TypeKind.Bool: return IlLiterals.Bool(false);
            case TypeKind.Char:
            case TypeKind.Int:
            case TypeKind.Long:
            case TypeKind.UInt:
                return IlLiterals.Int(0);
            case TypeKind.Float:
            case TypeKind.Double:
                return IlLiterals.Float(0f);
        }
        return IlLiterals.Null();
    }

    // Default values of the type arguments passed to the zero value of a generic struct (`S.new`)
    public List<IlExpr> StructTypeArgDefaults(TypeSym t)
    {
        var r = new List<IlExpr>();
        if (t.Kind == TypeKind.Tuple)
        {
            foreach (var a in t.Args!) r.Add(TypeArgDefaultValue(a));
            return r;
        }
        if (t.Decl != null && t.Decl.TypeParams.Count > 0 && t.Args != null)
        {
            foreach (var a in t.Args) r.Add(TypeArgDefaultValue(a));
        }
        return r;
    }

    // The value passed as a type argument's default: a type argument passes on what it received, a mutable struct passes its zero value and how to copy it (a descriptor), an enum passes a descriptor of its name table
    public IlExpr TypeArgDefaultValue(TypeSym t)
    {
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return passed;
        }
        if (NeedsTypeArgDescriptor(t))
            return new IlStructDescriptor(TypeName(t), StructTypeArgDefaults(t), HasToStringOverride(t), null);
        if (t.Kind == TypeKind.Enum)
        {
            var spec = EnumFormatSpec(t, false);
            if (spec != null) return new IlCall("__tcs_etd", One(IlLiterals.Str(spec)), null);
        }
        return DefaultIl(t);
    }

    // Structs for which a default value alone is not enough when used as a type argument: mutable (needs a way to copy) or has a ToString override
    public static bool NeedsTypeArgDescriptor(TypeSym t)
    {
        return IlTypes.IsMutableStruct(t) || (IlTypes.IsUserStruct(t) && HasToStringOverride(t));
    }

    public static bool HasToStringOverride(TypeSym t)
    {
        if (t.Decl == null) return false;
        var cands = t.Decl.Lookup("ToString");
        if (cands == null) return false;
        foreach (var c in cands)
        {
            if (c.Kind == SymKind.Method && c.ParamCount == 0 && !c.IsStatic) return true;
        }
        return false;
    }

    // ---- Struct copies ----

    // Copy of a value without an original expression (deconstruction component / foreach variable / lambda parameter)
    private IlExpr CopyIfMutableStruct(TypeSym? t, IlExpr value)
    {
        if (IlTypes.IsMutableStruct(t)) return new IlStructCopy(value, TypeName(t!), false, null);
        return value;
    }

    // A value known to be new from its IL shape
    private static bool IsFreshStructIl(IlExpr built)
    {
        switch (built)
        {
            case IlNewObj:
            case IlStructCopy:
            case IlWith:
            case IlTable:
                return true;
            case IlLit lit:
                return lit.Kind == IlLitKind.Null;
            case IlCall call:
                return call.Callee == "__tcs_dv" || call.Callee == "__tcs_nt" || call.Callee == "__tcs_tc";
            case IlNullableWrap wrap:
                return IsFreshStructIl(wrap.E);
            case IlParen paren:
                return IsFreshStructIl(paren.E);
        }
        return false;
    }

    // Whether the expression's value is not referenced from anywhere else (creation, with, tuple, assignment expression, switch expression)
    private static bool IsFreshStructValue(Node src)
    {
        switch (src.Kind)
        {
            case NodeKind.Paren:
            case NodeKind.Conv:
                return IsFreshStructValue(src.A!);
            case NodeKind.New:
            case NodeKind.Tuple:
            case NodeKind.Assign:
            case NodeKind.SwitchExpr:
                return true;
            case NodeKind.Call:
                return src.Text == "with";
        }
        return false;
    }

    // Receiver of a struct method / accessor: a C# "variable" (local / param / field / array element / this) is passed directly,
    // and an rvalue (property / List indexer / call result, etc.) is copied
    private IlExpr StructReceiverArg(Node recvSyntax, IlExpr recv)
    {
        // The receiver of ?. (the .Value of `S?`) is a copied value (changes are discarded)
        if (_condBinding != null && recvSyntax == _subNode) return ConditionalStructReceiver(recv, NaturalType(recvSyntax));
        if (IsStructVariableReceiver(recvSyntax)) return recv;
        return WrapStructCopy(recvSyntax, recv);
    }

    private bool IsStructVariableReceiver(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.This: return true;
            case NodeKind.Name: return e.Info == RefKind.Local || e.Info == RefKind.Field && IsWritableField(e.Sym);
            // The binding of `x?.F` / `x?[i]` is a variable if x is a reference type (x of type `T?` is a copy of the value).
            // A struct field is a variable only when the owning struct is a variable (a field of a property result is a value)
            case NodeKind.Member:
                if (e.Sym == null || e.Sym.Kind != SymKind.Field || !IsWritableField(e.Sym)) return false;
                if (e.Value == 1) return IsReferenceType(NaturalType(e.A!));
                return e.Sym.IsStatic || !NaturalType(e.A!).IsValueType || IsStructVariableReceiver(e.A!);
            case NodeKind.Index: return (e.A!.Type == null || e.A.Type.Kind == TypeKind.Array) && (e.Value != 1 || IsReferenceType(NaturalType(e.A)));
            case NodeKind.Paren:
            case NodeKind.Conv:
                return IsStructVariableReceiver(e.A!);
        }
        return false;
    }

    // A readonly field is a value outside the constructor of the declaring type (the static constructor if static) (C#'s defensive copy)
    private bool IsWritableField(Symbol? f)
    {
        if (f == null || !f.IsReadonly) return true;
        var fn = Ctx.CurrentFunction;
        return fn != null && fn.Kind == SymKind.Ctor && fn.Owner == f.Owner && fn.IsStatic == f.IsStatic;
    }

    // ---- Helpers ----

    private static List<IlStat> One(IlStat s)
    {
        var r = new List<IlStat>();
        r.Add(s);
        return r;
    }

    private static List<IlExpr> One(IlExpr e)
    {
        var r = new List<IlExpr>();
        r.Add(e);
        return r;
    }

    private static List<IlExpr> Two(IlExpr a, IlExpr b)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        r.Add(b);
        return r;
    }

    private static List<IlArm> Arm(IlExpr cond, IlBlock body)
    {
        var r = new List<IlArm>();
        r.Add(new IlArm(cond, body));
        return r;
    }

    // Replaces the Origin of an expression (elseif conditions and do-while conditions are evaluated at their own position)
    public static IlExpr WithExprOrigin(IlExpr e, IlPos? origin)
    {
        switch (e)
        {
            case IlLit x1: return x1 with { Origin = origin };
            case IlVar x2: return x2 with { Origin = origin };
            case IlField x3: return x3 with { Origin = origin };
            case IlIndex x4: return x4 with { Origin = origin };
            case IlLen x5: return x5 with { Origin = origin };
            case IlBin x6: return x6 with { Origin = origin };
            case IlUn x7: return x7 with { Origin = origin };
            case IlParen x8: return x8 with { Origin = origin };
            case IlTernary x10: return x10 with { Origin = origin };
            case IlCall x11: return x11 with { Origin = origin };
            case IlDynCall x12: return x12 with { Origin = origin };
            case IlInvoke x13: return x13 with { Origin = origin };
            case IlNewObj x14: return x14 with { Origin = origin };
            case IlTable x15: return x15 with { Origin = origin };
            case IlNewArray x16: return x16 with { Origin = origin };
            case IlNewQueue x17: return x17 with { Origin = origin };
            case IlNewHashSet x18: return x18 with { Origin = origin };
            case IlNumericConvert x19: return x19 with { Origin = origin };
            case IlRefCast x20: return x20 with { Origin = origin };
            case IlIsType x21: return x21 with { Origin = origin };
            case IlIsPrimType x22: return x22 with { Origin = origin };
            case IlIife x23: return x23 with { Origin = origin };
            case IlClosure x24: return x24 with { Origin = origin };
            case IlMethodGroup x25: return x25 with { Origin = origin };
            case IlStructCopy x26: return x26 with { Origin = origin };
            case IlNullableWrap x27: return x27 with { Origin = origin };
            case IlNullableHasValue x28: return x28 with { Origin = origin };
            case IlNullableValue x29: return x29 with { Origin = origin };
            case IlNullableGetOrDefault x30: return x30 with { Origin = origin };
            case IlLiftedBin x31: return x31 with { Origin = origin };
            case IlLiftedUn x32: return x32 with { Origin = origin };
            case IlCast x33: return x33 with { Origin = origin };
            case IlStructDescriptor x34: return x34 with { Origin = origin };
            case IlWith x35: return x35 with { Origin = origin };
        }
        return e;
    }
}

// Shape of a value-returning IIFE (TryGetValueChainShape)
public sealed class ValueChain
{
    public List<IlStat> Setup;
    public IlIf? Chain;
    public IlExpr? Tail;
    public bool Complete;

    public ValueChain(List<IlStat> setup, IlIf? chain, IlExpr? tail, bool complete)
    {
        Setup = setup;
        Chain = chain;
        Tail = tail;
        Complete = complete;
    }
}

// An assignment target with the receiver / index fixed in temps
public sealed class LoweredTarget
{
    public List<IlStat> Setup;
    public IlExpr Access;

    public LoweredTarget(List<IlStat> setup, IlExpr access)
    {
        Setup = setup;
        Access = access;
    }
}

// The starting point reached by following the struct fields of a value-type place (Stop is the starting node, Base is the reference-type prefix or variable, Index is the array index)
public sealed class StructSlot
{
    public Node Stop;
    public Node Base;
    public Node? Index;
    public bool IsVariable;

    public StructSlot(Node stop, Node baseNode, Node? index, bool isVariable)
    {
        Stop = stop;
        Base = baseNode;
        Index = index;
        IsVariable = isVariable;
    }
}

// Assignment target of a custom property
public sealed class PropTarget
{
    public List<IlStat> Setup;
    public IlExpr Recv;
    public string Name;
    public bool IsStatic;
    public string? StructOwner;
    public List<IlExpr> StaticArgs;

    public PropTarget(List<IlStat> setup, IlExpr recv, string name, bool isStatic, string? structOwner, List<IlExpr> staticArgs)
    {
        Setup = setup;
        Recv = recv;
        Name = name;
        IsStatic = isStatic;
        StructOwner = structOwner;
        StaticArgs = staticArgs;
    }
}

// Deconstruction target (declared local, _, assignment location, nested)
public sealed class DTarget
{
    public const int KDeclared = 0;
    public const int KDiscard = 1;
    public const int KPlace = 2;
    public const int KNested = 3;
    public int Kind;
    public string Name = "";
    public TypeSym? Type;
    public Node? Place;
    public List<DTarget> Items = new List<DTarget>();
    // Location fixed before the right side
    public IlExpr? Fixed;

    public static DTarget Declared(string name, TypeSym type)
    {
        var t = new DTarget();
        t.Kind = KDeclared;
        t.Name = name;
        t.Type = type;
        return t;
    }

    public static DTarget Discard()
    {
        var t = new DTarget();
        t.Kind = KDiscard;
        return t;
    }

    public static DTarget PlaceOf(Node place, TypeSym type)
    {
        var t = new DTarget();
        t.Kind = KPlace;
        t.Place = place;
        t.Type = type;
        return t;
    }

    public static DTarget Nested(List<DTarget> items)
    {
        var t = new DTarget();
        t.Kind = KNested;
        t.Items = items;
        return t;
    }
}

// Assignment of a deconstruction component
public sealed class DAssign
{
    public DTarget Target;
    public IlExpr Value;

    public DAssign(DTarget target, IlExpr value)
    {
        Target = target;
        Value = value;
    }
}

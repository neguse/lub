namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// The LINQ part of FuzzGenerator (TinySystem.Linq) + Split/Join/IsNullOrEmpty.
/// Forms that hit a fault are excluded structurally: Sum bounds the elements with % 1000 to
/// avoid overflow (Sum is checked), and Min/Max are only generated with a Count guard.
/// LINQ over a Dictionary, whose enumeration order is not fixed, is not generated either.
/// </summary>
internal sealed partial class FuzzGenerator
{
    private string PickList() => _listVars[_rng.Next(_listVars.Count)];

    // Lambda predicate. Sometimes captures an outer variable to exercise closure emit.
    private string Predicate(string v) => _rng.Next(5) switch
    {
        0 => $"{v} > {NextInt32()}",
        1 => $"{v} < {NextInt32()}",
        2 => $"({v} % {_rng.Next(1, 10)}) == 0",
        3 => $"{v} != {NextInt32()}",
        _ => $"{v} > {PickReadableIntVar()}",
    };

    private string LinqIntOrElse(int depth, Func<string> fallback)
    {
        if (_listVars.Count == 0)
            return fallback();
        var xs = PickList();
        var v = NextVar();
        return _rng.Next(8) switch
        {
            0 => $"{xs}.Select({v} => {v} % 1000).Sum()",
            1 => $"{xs}.Count({v} => {Predicate(v)})",
            2 => $"({xs}.Count > 0 ? {xs}.{(_rng.Next(2) == 0 ? "Max" : "Min")}() " +
                 $": {IntExpr(Math.Max(depth - 1, 0))})",
            3 => $"{xs}.Where({v} => {Predicate(v)}).Count",
            4 => $"{xs}.FirstOrDefault({v} => {Predicate(v)})",
            5 => $"{xs}.Sum({v} => {v} % 1000)",
            6 => $"{xs}.OrderBy{(_rng.Next(2) == 0 ? "" : "Descending")}" +
                 $"({v} => {v}).FirstOrDefault()",
            _ => $"{xs}.Select({v} => {v} + 1).Count",
        };
    }

    private string LinqBoolOrElse(Func<string> fallback)
    {
        if (_rng.Next(6) == 0)
            return $"string.IsNullOrEmpty({StringExpr(0)})";
        if (_listVars.Count == 0)
            return fallback();
        var xs = PickList();
        var v = NextVar();
        return _rng.Next(3) switch
        {
            0 => $"{xs}.Any()",
            1 => $"{xs}.Any({v} => {Predicate(v)})",
            _ => $"{xs}.All({v} => {Predicate(v)})",
        };
    }

    private string JoinOrElse(Func<string> fallback)
    {
        if (_listVars.Count == 0)
            return fallback();
        return $"string.Join(\"{Needle()}\", {PickList()})";
    }

    private string LinqStatement()
    {
        if (_rng.Next(2) == 0)
        {
            var s = PickStringVar();
            var p = NextVar();
            return $"foreach (var {p} in {s}.Split(\"{Needle()}\")) " +
                $"{{ Console.WriteLine({p}); }}";
        }
        if (_listVars.Count == 0)
            return SimpleAssign();
        var xs = PickList();
        var v = NextVar();
        var e = NextVar();
        var body = _rng.Next(2) == 0
            ? $"{PickIntVar()} += {e};"
            : $"Console.WriteLine({e});";
        return $"foreach (var {e} in {xs}.Where({v} => {Predicate(v)})) " +
            $"{{ {body} }}";
    }
}

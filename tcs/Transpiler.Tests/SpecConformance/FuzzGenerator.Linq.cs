namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// FuzzGenerator の LINQ 部 (TinySystem.Linq) + Split/Join/IsNullOrEmpty。
/// fault を踏む形は構造的に排除する: Sum は要素を % 1000 で有界化して
/// overflow (Sum は checked) を避け、Min/Max は Count ガードつきだけ。
/// 列挙順が確定しない Dictionary への LINQ も生成しない。
/// </summary>
internal sealed partial class FuzzGenerator
{
    private string PickList() => _listVars[_rng.Next(_listVars.Count)];

    // ラムダ述語。たまに外側変数を捕捉して closure emit を踏む
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

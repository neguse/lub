using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 型 table (class / struct / record / enum) の chunk-local キャッシュ。
// 型名は global にも公開したまま (host・hot reload chunk・registry が参照する)、
// chunk 内の参照は upvalue 経由にして global lookup を省く。
//
// 前提: 同じ chunk の中でだけ同一性が保たれること。次の用途では使わない
// (TypeLocalBudget = 0 のまま):
//   - module / snapshot の define chunk (型 table は registry が所有し、
//     define は読み取り専用 _ENV 越しに解決する)
//   - hot reload の v2 chunk (旧 table へ identity を戻すために global 解決が
//     必要。HotReload.cs)
public partial class LuaEmitter
{
    private const string TypeLocalsPlaceholder = "-- tcs:type-locals";

    // chunk 直下の local は 200 個が上限。prelude の helper (約 35) と
    // 利用者 prelude / top-level 文の local の余地を残した型数の上限
    private const int MaxTypeLocals = 100;

    private readonly List<string> _typeLocals = [];

    /// <summary>chunk-local に cache する型 table の最大数。0 なら cache しない。</summary>
    public int TypeLocalBudget { get; set; }

    /// <summary>emit 対象 tree 群に対する既定の budget。top-level 文の local は
    /// chunk 直下に出るので、その分 (過大評価で可) を差し引く。</summary>
    public static int DefaultTypeLocalBudget(IEnumerable<SyntaxTree> trees)
    {
        var topLevelLocals = trees
            .SelectMany(t => t.GetCompilationUnitRoot().Members
                .OfType<GlobalStatementSyntax>())
            .Sum(g => g.DescendantNodes().Count(n =>
                n is VariableDeclaratorSyntax or ForEachStatementSyntax
                    or SingleVariableDesignationSyntax));
        return Math.Max(0, MaxTypeLocals - topLevelLocals);
    }

    // `Name = {}` の宣言行。budget 内なら chunk 先頭で forward 宣言した local
    // へ代入し、global にも同じ table を公開する。forward 宣言なので宣言順・
    // ファイル順に関係なく全 method body から upvalue で引ける。
    private void AppendTypeTableDecl(string name)
    {
        if (_typeLocals.Contains(name)
            || (_typeLocals.Count < TypeLocalBudget && _headerEmitted))
        {
            if (!_typeLocals.Contains(name)) _typeLocals.Add(name);
            AppendLine($"{name} = {{}}; _ENV.{name} = {name}");
        }
        else
            AppendLine($"{name} = {{}}");
    }
}

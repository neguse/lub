// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/SdfPanel.hx と対)。
// Haxe 版の Reflect.field/setField によるスキーマ走査は、SdfNode が保持する
// Dictionary<string, object> の文字列 index + ContainsKey に置き換える。
// フィールド列挙は Haxe 版と同じくコード内の固定順なので widget 並びも同一。
// `params` は C# 予約語のため opParams と改名 (private なので API 面に影響なし)。

using System.Collections.Generic;
using static Lub;

/// <summary>SDF ツリー(素の data)から ImGui のチューニング UI を自動生成
/// する。widget はノードのフィールドを in-place に書き換え、どれかが変わったら
/// true を返す(呼び側はそれを remesh のトリガにする)。hot reload すると
/// コードからツリーが再構築されるので、パネル編集はリロードまでの一時
/// オーバーレイ。
/// <code>
/// if (SdfPanel.draw(tree))
///     meshDirty = true;
/// </code></summary>
public static class SdfPanel
{
    /// <summary>ルートから widget 群を描く。編集があれば true。</summary>
    public static bool Draw(SdfNode root)
    {
        return Node(root.Data, "/");
    }

    // ImGui の ID はツリー内のパスから作る (##/a/c 等)。訪問順カウンタだと
    // ノードを畳んだとき後続の ID がズレて開閉状態が飛ぶ。

    private static bool Num(Dictionary<string, object> n, string field,
        double speed, string path)
    {
        double v = (double)n[field];
        double nv = Ui.DragFloat(field + "##" + path, v, speed);
        if (nv == v)
            return false;
        n[field] = nv;
        return true;
    }

    private static bool Num01(Dictionary<string, object> n, string field,
        string path)
    {
        double v = (double)n[field];
        double nv = Ui.SliderFloat(field + "##" + path, v, 0, 1);
        if (nv == v)
            return false;
        n[field] = nv;
        return true;
    }

    private static bool Color(Dictionary<string, object> n, string path)
    {
        double cr = (double)n["cr"];
        double cg = (double)n["cg"];
        double cb = (double)n["cb"];
        Ui.ColorEdit3("albedo##" + path, cr, cg, cb, out var r, out var g,
            out var b);
        if (r == cr && g == cg && b == cb)
            return false;
        n["cr"] = r;
        n["cg"] = g;
        n["cb"] = b;
        return true;
    }

    private static bool OpParams(Dictionary<string, object> n, string path)
    {
        bool changed = false;
        switch ((string)n["op"])
        {
            case "sphere":
                changed = Num(n, "r", 0.005, path);
                break;
            case "box":
                changed = Num(n, "hx", 0.005, path) || changed;
                changed = Num(n, "hy", 0.005, path) || changed;
                changed = Num(n, "hz", 0.005, path) || changed;
                break;
            case "capsule":
                foreach (var f in new List<string>
                    { "ax", "ay", "az", "bx", "by", "bz" })
                    changed = Num(n, f, 0.01, path) || changed;
                changed = Num(n, "r", 0.005, path) || changed;
                break;
            case "torus":
                changed = Num(n, "rmajor", 0.005, path) || changed;
                changed = Num(n, "rminor", 0.005, path) || changed;
                break;
            case "move":
                changed = Num(n, "x", 0.01, path) || changed;
                changed = Num(n, "y", 0.01, path) || changed;
                changed = Num(n, "z", 0.01, path) || changed;
                break;
            case "scale":
                changed = Num(n, "s", 0.005, path);
                break;
            case "smin":
            case "ssub":
                changed = Num(n, "k", 0.002, path);
                break;
            case "paint":
                changed = Color(n, path) || changed;
                changed = Num01(n, "metallic", path) || changed;
                changed = Num01(n, "roughness", path) || changed;
                break;
            case "bone":
                changed = Num(n, "px", 0.01, path) || changed;
                changed = Num(n, "py", 0.01, path) || changed;
                changed = Num(n, "pz", 0.01, path) || changed;
                break;
            default:
                // rotate (quat は直接いじらない) / mirror_x / union / ...
                break;
        }
        return changed;
    }

    private static bool Node(Dictionary<string, object> n, string path)
    {
        string op = (string)n["op"];
        string label = op;
        if (n.ContainsKey("name"))
            label = op + " (" + (string)n["name"] + ")";
        label = label + "##" + path;
        bool changed = false;
        if (Ui.TreeNode(label, true))
        {
            changed = OpParams(n, path);
            if (n.ContainsKey("c"))
                changed = Node((Dictionary<string, object>)n["c"],
                    path + "c/") || changed;
            if (n.ContainsKey("a"))
                changed = Node((Dictionary<string, object>)n["a"],
                    path + "a/") || changed;
            if (n.ContainsKey("b"))
                changed = Node((Dictionary<string, object>)n["b"],
                    path + "b/") || changed;
            Ui.TreePop();
        }
        return changed;
    }
}

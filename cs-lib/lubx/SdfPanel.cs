// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/SdfPanel.hx と対)。
// Haxe 版の Reflect.field/setField によるスキーマ走査は、SdfNode が保持する
// Dictionary<string, object> の文字列 index + ContainsKey に置き換える。
// フィールド列挙は Haxe 版と同じくコード内の固定順なので widget 並びも同一。
// `params` は C# 予約語のため opParams と改名 (private なので API 面に影響なし)。

using System.Collections.Generic;

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
    public static bool draw(SdfNode root)
    {
        return node(root.data, "/");
    }

    // ImGui の ID はツリー内のパスから作る (##/a/c 等)。訪問順カウンタだと
    // ノードを畳んだとき後続の ID がズレて開閉状態が飛ぶ。

    private static bool num(Dictionary<string, object> n, string field,
        float speed, string path)
    {
        float v = (float)n[field];
        float nv = Ui.ui_drag_float(field + "##" + path, v, speed);
        if (nv == v)
            return false;
        n[field] = nv;
        return true;
    }

    private static bool num01(Dictionary<string, object> n, string field,
        string path)
    {
        float v = (float)n[field];
        float nv = Ui.ui_slider_float(field + "##" + path, v, 0, 1);
        if (nv == v)
            return false;
        n[field] = nv;
        return true;
    }

    private static bool color(Dictionary<string, object> n, string path)
    {
        float cr = (float)n["cr"];
        float cg = (float)n["cg"];
        float cb = (float)n["cb"];
        Ui.ui_color_edit3("albedo##" + path, cr, cg, cb, out var r, out var g,
            out var b);
        if (r == cr && g == cg && b == cb)
            return false;
        n["cr"] = r;
        n["cg"] = g;
        n["cb"] = b;
        return true;
    }

    private static bool opParams(Dictionary<string, object> n, string path)
    {
        bool changed = false;
        switch ((string)n["op"])
        {
            case "sphere":
                changed = num(n, "r", 0.005f, path);
                break;
            case "box":
                changed = num(n, "hx", 0.005f, path) || changed;
                changed = num(n, "hy", 0.005f, path) || changed;
                changed = num(n, "hz", 0.005f, path) || changed;
                break;
            case "capsule":
                foreach (var f in new List<string>
                    { "ax", "ay", "az", "bx", "by", "bz" })
                    changed = num(n, f, 0.01f, path) || changed;
                changed = num(n, "r", 0.005f, path) || changed;
                break;
            case "torus":
                changed = num(n, "rmajor", 0.005f, path) || changed;
                changed = num(n, "rminor", 0.005f, path) || changed;
                break;
            case "move":
                changed = num(n, "x", 0.01f, path) || changed;
                changed = num(n, "y", 0.01f, path) || changed;
                changed = num(n, "z", 0.01f, path) || changed;
                break;
            case "scale":
                changed = num(n, "s", 0.005f, path);
                break;
            case "smin":
            case "ssub":
                changed = num(n, "k", 0.002f, path);
                break;
            case "paint":
                changed = color(n, path) || changed;
                changed = num01(n, "metallic", path) || changed;
                changed = num01(n, "roughness", path) || changed;
                break;
            case "bone":
                changed = num(n, "px", 0.01f, path) || changed;
                changed = num(n, "py", 0.01f, path) || changed;
                changed = num(n, "pz", 0.01f, path) || changed;
                break;
            default:
                // rotate (quat は直接いじらない) / mirror_x / union / ...
                break;
        }
        return changed;
    }

    private static bool node(Dictionary<string, object> n, string path)
    {
        string op = (string)n["op"];
        string label = op;
        if (n.ContainsKey("name"))
            label = op + " (" + (string)n["name"] + ")";
        label = label + "##" + path;
        bool changed = false;
        if (Ui.ui_tree_node(label, true))
        {
            changed = opParams(n, path);
            if (n.ContainsKey("c"))
                changed = node((Dictionary<string, object>)n["c"],
                    path + "c/") || changed;
            if (n.ContainsKey("a"))
                changed = node((Dictionary<string, object>)n["a"],
                    path + "a/") || changed;
            if (n.ContainsKey("b"))
                changed = node((Dictionary<string, object>)n["b"],
                    path + "b/") || changed;
            Ui.ui_tree_pop();
        }
        return changed;
    }
}

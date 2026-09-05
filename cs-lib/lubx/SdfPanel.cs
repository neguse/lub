// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/SdfPanel.hx と対)。
// Haxe 版の Reflect.field/setField によるスキーマ走査は、typed な SdfNode の
// Params (op ごとの数値列) の in-place 書き換えに置き換える。
// フィールド列挙は Haxe 版と同じくコード内の固定順なので widget 並びも同一。
using System.Collections.Generic;
using static Lub;

/// <summary>SDF ツリーから ImGui のチューニング UI を自動生成する。widget は
/// ノードの Params を in-place に書き換え、どれかが変わったら true を返す
/// (呼び側はそれを remesh のトリガにする)。hot reload するとコードから
/// ツリーが再構築されるので、パネル編集はリロードまでの一時オーバーレイ。
/// <code>
/// if (SdfPanel.Draw(tree))
///     meshDirty = true;
/// </code></summary>
public static class SdfPanel
{
    /// <summary>ルートから widget 群を描く。編集があれば true。</summary>
    public static bool Draw(SdfNode root)
    {
        return Node(root, "/");
    }

    // ImGui の ID はツリー内のパスから作る (##/a/c 等)。訪問順カウンタだと
    // ノードを畳んだとき後続の ID がズレて開閉状態が飛ぶ。
    private static bool Num(SdfNode n, int index, string field, double speed,
        string path)
    {
        double v = n.Params[index];
        double nv = Ui.DragFloat(field + "##" + path, v, speed);
        if (nv == v)
            return false;
        n.Params[index] = nv;
        return true;
    }

    private static bool Num01(SdfNode n, int index, string field, string path)
    {
        double v = n.Params[index];
        double nv = Ui.SliderFloat(field + "##" + path, v, 0, 1);
        if (nv == v)
            return false;
        n.Params[index] = nv;
        return true;
    }

    private static bool Color(SdfNode n, string path)
    {
        double cr = n.Params[0];
        double cg = n.Params[1];
        double cb = n.Params[2];
        Ui.ColorEdit3("albedo##" + path, cr, cg, cb, out var r, out var g,
            out var b);
        if (r == cr && g == cg && b == cb)
            return false;
        n.Params[0] = r;
        n.Params[1] = g;
        n.Params[2] = b;
        return true;
    }

    private static bool Nums(SdfNode n, List<string> fields, double speed,
        string path)
    {
        bool changed = false;
        for (var i = 0; i < fields.Count; i++)
            changed = Num(n, i, fields[i], speed, path) || changed;
        return changed;
    }

    private static bool OpParams(SdfNode n, string path)
    {
        bool changed = false;
        switch (n.Op)
        {
            case Lub.Mesh.SdfOp.Sphere:
                changed = Num(n, 0, "r", 0.005, path);
                break;
            case Lub.Mesh.SdfOp.Box:
                changed = Nums(n, new List<string> { "hx", "hy", "hz" }, 0.005,
                    path);
                break;
            case Lub.Mesh.SdfOp.Capsule:
                changed = Nums(n,
                    new List<string> { "ax", "ay", "az", "bx", "by", "bz" },
                    0.01, path);
                changed = Num(n, 6, "r", 0.005, path) || changed;
                break;
            case Lub.Mesh.SdfOp.Torus:
                changed = Nums(n, new List<string> { "rmajor", "rminor" },
                    0.005, path);
                break;
            case Lub.Mesh.SdfOp.Move:
                changed = Nums(n, new List<string> { "x", "y", "z" }, 0.01,
                    path);
                break;
            case Lub.Mesh.SdfOp.Scale:
                changed = Num(n, 0, "s", 0.005, path);
                break;
            case Lub.Mesh.SdfOp.Smin:
            case Lub.Mesh.SdfOp.Ssub:
                changed = Num(n, 0, "k", 0.002, path);
                break;
            case Lub.Mesh.SdfOp.Paint:
                changed = Color(n, path) || changed;
                changed = Num01(n, 3, "metallic", path) || changed;
                changed = Num01(n, 4, "roughness", path) || changed;
                break;
            case Lub.Mesh.SdfOp.Bone:
                changed = Nums(n, new List<string> { "px", "py", "pz" }, 0.01,
                    path);
                break;
            default:
                // rotate (quat は直接いじらない) / mirror_x / union / ...
                break;
        }
        return changed;
    }

    private static bool Node(SdfNode n, string path)
    {
        string op = Sdf.OpName(n.Op);
        string label = op;
        if (n.Name != null)
            label = op + " (" + n.Name + ")";
        label = label + "##" + path;
        bool changed = false;
        if (Ui.TreeNode(label, true))
        {
            changed = OpParams(n, path);
            if (n.C != null)
                changed = Node(n.C, path + "c/") || changed;
            if (n.A != null)
                changed = Node(n.A, path + "a/") || changed;
            if (n.B != null)
                changed = Node(n.B, path + "b/") || changed;
            Ui.TreePop();
        }
        return changed;
    }
}

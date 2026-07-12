// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Sdf.hx と対)。
// Haxe 版は abstract SdfNode(Dynamic) で「値そのものが wire table」だが、
// C# 版は SdfNode class が Dictionary<string, object> (= sdf_mesh (C) が読む
// 素の data table) を保持し、builder が Haxe 版と同キー・同値型で構築する。
// 子参照 (c / a / b) には子の dict を入れるので、ツリー全体が素の table。
// 省略可能フィールドはデフォルト引数でなく nullable + ?? で受け、未指定
// キーは dict に入れない (TCS1003: Lua table への null 保存を避ける)。
// paint の bit 演算 (rgb >> 16 & 0xFF) は tcs 未対応なので Color.hex と
// 同じ Math.Floor 分解で書く。
// メンバー名は Haxe 版 API と揃える (--no-naming-check でビルドされる)。

using System;
using System.Collections.Generic;

/// <summary>SDF ツリーのノード。中身は `sdf_mesh` (C) が読む素の data table
/// (schema は `src/sdf.h` 参照)。メソッドは常に新しいノードを返す
/// (イミュータブル)。部分ツリーは共有してよい。</summary>
public class SdfNode
{
    /// <summary>wire table 本体。`Sdf.mesh` と `SdfPanel` が直接歩く。</summary>
    public Dictionary<string, object> data;

    public SdfNode(Dictionary<string, object> data)
    {
        this.data = data;
    }

    public SdfNode move(double x, double y, double z) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "move",
            ["x"] = x,
            ["y"] = y,
            ["z"] = z,
            ["c"] = data,
        });

    /// <summary>`axis` 回りに `rad` ラジアン回す。</summary>
    public SdfNode rotate(Vec3 axis, double rad)
    {
        var q = Quat.fromAxisAngle(axis, rad);
        return new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "rotate",
            ["qx"] = q.x,
            ["qy"] = q.y,
            ["qz"] = q.z,
            ["qw"] = q.w,
            ["c"] = data,
        });
    }

    /// <summary>uniform スケール (`s` &gt; 0)。</summary>
    public SdfNode scale(double s) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "scale",
            ["s"] = s,
            ["c"] = data,
        });

    /// <summary>X 対称 (|x| 折り畳み)。</summary>
    public SdfNode mirrorX() =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "mirror_x",
            ["c"] = data,
        });

    /// <summary>サブツリーに材質 (albedo + metallic/roughness) を与える。
    /// innermost の paint が勝つ。`rgb` は 0xRRGGBB (metallic 省略で 0.0、
    /// roughness 省略で 0.8)。smin/ssub では距離と同じ blend で材質も混ざり、
    /// subtract/ssub の切断面には cutter の材質が出る。</summary>
    public SdfNode paint(int rgb, double? metallic = null,
        double? roughness = null) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "paint",
            ["cr"] = Math.Floor(rgb / 65536.0) % 256 / 255.0,
            ["cg"] = Math.Floor(rgb / 256.0) % 256 / 255.0,
            ["cb"] = rgb % 256 / 255.0,
            ["metallic"] = metallic ?? 0.0,
            ["roughness"] = roughness ?? 0.8,
            ["c"] = data,
        });

    public SdfNode union(SdfNode b) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "union",
            ["a"] = data,
            ["b"] = b.data,
        });

    /// <summary>smooth union。`k` が blend 幅。</summary>
    public SdfNode smin(SdfNode b, double k) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "smin",
            ["k"] = k,
            ["a"] = data,
            ["b"] = b.data,
        });

    /// <summary>`b` をくり抜く。</summary>
    public SdfNode subtract(SdfNode b) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "subtract",
            ["a"] = data,
            ["b"] = b.data,
        });

    /// <summary>smooth subtraction。`k` が縁の丸まり幅。</summary>
    public SdfNode ssub(SdfNode b, double k) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "ssub",
            ["k"] = k,
            ["a"] = data,
            ["b"] = b.data,
        });

    public SdfNode intersect(SdfNode b) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "intersect",
            ["a"] = data,
            ["b"] = b.data,
        });

    /// <summary>サブツリーを skinning 部位として宣言する。`pivot` は関節位置
    /// (model 空間)。メッシュ化時に頂点ごとの部位距離から重みが焼かれる。
    /// mirror_x の内側に置くと両側に重みが付くのに pivot が片側になるので、
    /// 動かす bone は mirror の外で個別に置くこと。</summary>
    public SdfNode bone(string name, Vec3 pivot) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "bone",
            ["name"] = name,
            ["px"] = pivot.x,
            ["py"] = pivot.y,
            ["pz"] = pivot.z,
            ["c"] = data,
        });
}

/// <summary>SDF モデリングの builder。プリミティブを作り、`SdfNode` の
/// メソッドチェーンで変形・合成し、`Sdf.mesh` でメッシュ化する:
/// <code>
/// var body = Sdf.sphere(0.72).move(0, -0.42, 0);
/// var head = Sdf.sphere(0.46).move(0, 0.48, 0);
/// var mesh = Sdf.mesh(body.smin(head, 0.22), 64);
/// </code></summary>
public static class Sdf
{
    public static SdfNode sphere(double r) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "sphere",
            ["r"] = r,
        });

    /// <summary>half extents の直方体。</summary>
    public static SdfNode box(double hx, double hy, double hz) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "box",
            ["hx"] = hx,
            ["hy"] = hy,
            ["hz"] = hz,
        });

    /// <summary>線分 `a`-`b` を軸とする半径 `r` のカプセル。</summary>
    public static SdfNode capsule(Vec3 a, Vec3 b, double r) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "capsule",
            ["ax"] = a.x,
            ["ay"] = a.y,
            ["az"] = a.z,
            ["bx"] = b.x,
            ["by"] = b.y,
            ["bz"] = b.z,
            ["r"] = r,
        });

    /// <summary>XZ 平面に寝たトーラス。</summary>
    public static SdfNode torus(double rMajor, double rMinor) =>
        new SdfNode(new Dictionary<string, object>
        {
            ["op"] = "torus",
            ["rmajor"] = rMajor,
            ["rminor"] = rMinor,
        });

    /// <summary>ツリーをメッシュ化する。`n` は最長軸の cell 数 (bounds は
    /// 自動)。bone ノードがあれば skinning 情報も焼かれる
    /// (`skinK` = 重みの blend 幅)。</summary>
    public static MeshData mesh(SdfNode root, int n, double? skinK = null) =>
        Mesh.sdf_mesh(new Dictionary<string, object>
        {
            ["version"] = 1,
            ["root"] = root.data,
        }, n, skinK);
}

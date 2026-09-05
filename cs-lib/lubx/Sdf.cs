// 実装ライブラリ lubx の Sdf。
// typed な木 (op / params / 子参照) を持ち、Sdf.Mesh で C API の
// 平らな node 配列 (Lub.Mesh.SdfMesh の SdfNodeDesc) に落とす。
// paint の bit 演算 (rgb >> 16 & 0xFF) は tcs 未対応なので Color.hex と
// 同じ Math.Floor 分解で書く。
using System;
using System.Collections.Generic;
using static Lub;

/// <summary>SDF ツリーのノード。Params は op ごとの数値列 (並びは
/// SdfNodeDesc と同じ)、C は 1 子の op の子、A / B は 2 子の op の子。
/// builder のメソッドは常に新しいノードを返す (イミュータブル)。部分ツリーは
/// 共有してよい。SdfPanel は Params を in-place に書き換える。</summary>
public class SdfNode
{
    public Lub.Mesh.SdfOp Op;
    public List<float> Params;
    public string? Name;
    public SdfNode? C;
    public SdfNode? A;
    public SdfNode? B;

    public SdfNode(Lub.Mesh.SdfOp op, List<float> parameters)
    {
        this.Op = op;
        this.Params = parameters;
    }

    static SdfNode Unary(Lub.Mesh.SdfOp op, List<float> parameters, SdfNode c)
    {
        var n = new SdfNode(op, parameters);
        n.C = c;
        return n;
    }

    static SdfNode Binary(Lub.Mesh.SdfOp op, List<float> parameters, SdfNode a,
        SdfNode b)
    {
        var n = new SdfNode(op, parameters);
        n.A = a;
        n.B = b;
        return n;
    }

    public SdfNode Move(float x, float y, float z) =>
        Unary(Lub.Mesh.SdfOp.Move, new List<float> { x, y, z }, this);

    /// <summary>`axis` 回りに `rad` ラジアン回す。</summary>
    public SdfNode Rotate(Vec3 axis, float rad)
    {
        var q = Quat.FromAxisAngle(axis, rad);
        return Unary(Lub.Mesh.SdfOp.Rotate, new List<float> { q.X, q.Y, q.Z, q.W },
            this);
    }

    /// <summary>uniform スケール (`s` &gt; 0)。</summary>
    public SdfNode Scale(float s) =>
        Unary(Lub.Mesh.SdfOp.Scale, new List<float> { s }, this);

    /// <summary>X 対称 (|x| 折り畳み)。</summary>
    public SdfNode MirrorX() =>
        Unary(Lub.Mesh.SdfOp.MirrorX, new List<float>(), this);

    /// <summary>サブツリーに材質 (albedo + metallic/roughness) を与える。
    /// innermost の paint が勝つ。`rgb` は 0xRRGGBB (metallic 省略で 0.0、
    /// roughness 省略で 0.8)。smin/ssub では距離と同じ blend で材質も混ざり、
    /// subtract/ssub の切断面には cutter の材質が出る。</summary>
    public SdfNode Paint(int rgb, float? metallic = null,
        float? roughness = null) =>
        Unary(Lub.Mesh.SdfOp.Paint, new List<float>
        {
            (float)Math.Floor(rgb / 65536.0f) % 256 / 255.0f,
            (float)Math.Floor(rgb / 256.0f) % 256 / 255.0f,
            rgb % 256 / 255.0f,
            metallic ?? 0.0f,
            roughness ?? 0.8f,
        }, this);

    public SdfNode Union(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Union, new List<float>(), this, b);

    /// <summary>smooth union。`k` が blend 幅。</summary>
    public SdfNode Smin(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Smin, new List<float> { k }, this, b);

    /// <summary>`b` をくり抜く。</summary>
    public SdfNode Subtract(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Subtract, new List<float>(), this, b);

    /// <summary>smooth subtraction。`k` が縁の丸まり幅。</summary>
    public SdfNode Ssub(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Ssub, new List<float> { k }, this, b);

    public SdfNode Intersect(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Intersect, new List<float>(), this, b);

    /// <summary>サブツリーを skinning 部位として宣言する。`pivot` は関節位置
    /// (model 空間)。メッシュ化時に頂点ごとの部位距離から重みが焼かれる。
    /// mirror_x の内側に置くと両側に重みが付くのに pivot が片側になるので、
    /// 動かす bone は mirror の外で個別に置くこと。</summary>
    public SdfNode Bone(string name, Vec3 pivot)
    {
        var n = Unary(Lub.Mesh.SdfOp.Bone,
            new List<float> { pivot.X, pivot.Y, pivot.Z }, this);
        n.Name = name;
        return n;
    }
}

/// <summary>SDF モデリングの builder。プリミティブを作り、`SdfNode` の
/// メソッドチェーンで変形・合成し、`Sdf.Mesh` でメッシュ化する:
/// <code>
/// var body = Sdf.Sphere(0.72).Move(0, -0.42, 0);
/// var head = Sdf.Sphere(0.46).Move(0, 0.48, 0);
/// var mesh = Sdf.Mesh(body.Smin(head, 0.22), 64);
/// </code></summary>
public static class Sdf
{
    public static SdfNode Sphere(float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Sphere, new List<float> { r });

    /// <summary>half extents の直方体。</summary>
    public static SdfNode Box(float hx, float hy, float hz) =>
        new SdfNode(Lub.Mesh.SdfOp.Box, new List<float> { hx, hy, hz });

    /// <summary>線分 `a`-`b` を軸とする半径 `r` のカプセル。</summary>
    public static SdfNode Capsule(Vec3 a, Vec3 b, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Capsule,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r });

    /// <summary>XZ 平面に寝たトーラス。</summary>
    public static SdfNode Torus(float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.Torus, new List<float> { rMajor, rMinor });

    /// <summary>op の表示名 (SdfPanel のラベル用)。</summary>
    public static string OpName(Lub.Mesh.SdfOp op)
    {
        switch (op)
        {
            case Lub.Mesh.SdfOp.Sphere: return "sphere";
            case Lub.Mesh.SdfOp.Box: return "box";
            case Lub.Mesh.SdfOp.Capsule: return "capsule";
            case Lub.Mesh.SdfOp.Torus: return "torus";
            case Lub.Mesh.SdfOp.Move: return "move";
            case Lub.Mesh.SdfOp.Rotate: return "rotate";
            case Lub.Mesh.SdfOp.Scale: return "scale";
            case Lub.Mesh.SdfOp.MirrorX: return "mirror_x";
            case Lub.Mesh.SdfOp.Paint: return "paint";
            case Lub.Mesh.SdfOp.Bone: return "bone";
            case Lub.Mesh.SdfOp.Union: return "union";
            case Lub.Mesh.SdfOp.Smin: return "smin";
            case Lub.Mesh.SdfOp.Subtract: return "subtract";
            case Lub.Mesh.SdfOp.Ssub: return "ssub";
            case Lub.Mesh.SdfOp.Intersect: return "intersect";
            default: return "unknown";
        }
    }

    // 木を post-order で平らな配列に落とし、その node の index を返す。
    static int Flatten(SdfNode node, List<SdfNodeDesc> nodes)
    {
        var d = new SdfNodeDesc();
        d.Op = node.Op;
        d.Params = node.Params;
        if (node.Name != null)
            d.Name = node.Name;
        if (node.C != null)
            d.A = Flatten(node.C, nodes);
        if (node.A != null)
            d.A = Flatten(node.A, nodes);
        if (node.B != null)
            d.B = Flatten(node.B, nodes);
        nodes.Add(d);
        return nodes.Count - 1;
    }

    /// <summary>ツリーをメッシュ化する。`n` は最長軸の cell 数 (bounds は
    /// 自動)。bone ノードがあれば skinning 情報も焼かれる
    /// (`skinK` = 重みの blend 幅)。</summary>
    public static MeshData Mesh(SdfNode root, int n, float? skinK = null)
    {
        var nodes = new List<SdfNodeDesc>();
        var r = Flatten(root, nodes);
        return Lub.Mesh.SdfMesh(nodes, r, n, skinK);
    }
}

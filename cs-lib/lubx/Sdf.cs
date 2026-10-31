// 実装ライブラリ lubx の Sdf。
// typed な木 (op / params / 子参照) を持ち、Sdf.Mesh で C API の平らな node 配列 (Lub.Mesh.SdfMesh の SdfNodeDesc) に落とす。
// paint の bit 演算 (rgb >> 16 & 0xFF) は tcs 未対応なので Color.hex と同じ Math.Floor 分解で書く。
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>SDF ツリーのノード。
/// Params は op ごとの数値列 (並びは SdfNodeDesc と同じ)、C は 1 子の op の子、A / B は 2 子の op の子。
/// builder のメソッドは常に新しいノードを返す (イミュータブル)。
/// 部分ツリーは共有してよい。
/// SdfPanel は Params を in-place に書き換える。
/// </summary>
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

    /// <summary>`axis` 回りに `rad` ラジアン回す。
    /// </summary>
    public SdfNode Rotate(Vec3 axis, float rad)
    {
        var q = Quat.FromAxisAngle(axis, rad);
        return Unary(Lub.Mesh.SdfOp.Rotate, new List<float> { q.X, q.Y, q.Z, q.W },
            this);
    }

    /// <summary>uniform スケール (`s` &gt; 0)。
    /// </summary>
    public SdfNode Scale(float s) =>
        Unary(Lub.Mesh.SdfOp.Scale, new List<float> { s }, this);

    /// <summary>X 対称 (|x| 折り畳み)。
    /// </summary>
    public SdfNode MirrorX() =>
        Unary(Lub.Mesh.SdfOp.MirrorX, new List<float>(), this);

    /// <summary>Y 対称 (|y| 折り畳み)。
    /// </summary>
    public SdfNode MirrorY() =>
        Unary(Lub.Mesh.SdfOp.MirrorY, new List<float>(), this);

    /// <summary>Z 対称 (|z| 折り畳み)。
    /// </summary>
    public SdfNode MirrorZ() =>
        Unary(Lub.Mesh.SdfOp.MirrorZ, new List<float>(), this);

    /// <summary>原点で切り開き、各軸に `hx` / `hy` / `hz` ずつ引き伸ばす (間は断面をそのまま伸ばす)。
    /// 球から角丸の箱、トーラスから競技場の形などを作る。
    /// </summary>
    public SdfNode Elongate(float hx, float hy, float hz) =>
        Unary(Lub.Mesh.SdfOp.Elongate, new List<float> { hx, hy, hz }, this);

    /// <summary>Y 軸回りにねじる (高さ 1 あたり `k` ラジアン)。
    /// 距離は厳密でなくなる (強くねじると面が荒れる)。
    /// </summary>
    public SdfNode Twist(float k) =>
        Unary(Lub.Mesh.SdfOp.Twist, new List<float> { k }, this);

    /// <summary>X 軸に沿って +Y 側へ曲げる (長さ 1 あたり `k` ラジアン)。
    /// 距離は厳密でなくなる (強く曲げると面が荒れる)。
    /// </summary>
    public SdfNode Bend(float k) =>
        Unary(Lub.Mesh.SdfOp.Bend, new List<float> { k }, this);

    /// <summary>間隔 `sx` / `sy` / `sz` で、各軸の両側へ `nx` / `ny` / `nz` 個ずつ並べる (原点の 1 個を含めて 2n+1 個)。
    /// 間隔 0 の軸は並べない。
    /// 子は間隔の半分に収まる大きさにする (はみ出すと隣と継ぎ目が出る)。
    /// </summary>
    public SdfNode Repeat(float sx, float sy, float sz, int nx, int ny, int nz) =>
        Unary(Lub.Mesh.SdfOp.Repeat,
            new List<float> { sx, sy, sz, nx, ny, nz }, this);

    /// <summary>Y 軸回りに `n` 個並べる (1 個目は +X 側の子そのもの)。
    /// 子は +X 側に置き、隣の扇形 (360/n 度) からはみ出さない大きさにする。
    /// 花びら・歯車の歯・光線などに使う。
    /// </summary>
    public SdfNode PolarRepeat(int n) =>
        Unary(Lub.Mesh.SdfOp.PolarRepeat, new List<float> { n }, this);

    /// <summary>子の z = 0 の断面 (XY 平面) を、X を `o` だけずらして Y 軸回りに回した回転体 (旋盤)。
    /// 断面は x &gt;= -o 側に置く。
    /// 壺・カップ・瓶・ドーナツ形などを断面から作る。
    /// </summary>
    public SdfNode Revolve(float o) =>
        Unary(Lub.Mesh.SdfOp.Revolve, new List<float> { o }, this);

    /// <summary>子の z = 0 の断面 (XY 平面) を、Z 方向に半分の厚さ `h` で押し出す (角は立ったまま、Round で丸める)。
    /// </summary>
    public SdfNode Extrude(float h) =>
        Unary(Lub.Mesh.SdfOp.Extrude, new List<float> { h }, this);

    /// <summary>表面を fbm (value noise を `octaves` 段重ねたもの、1〜8) で最大 `amp` だけ凸凹にする。
    /// `freq` は 1 段目の細かさ (長さ 1 あたりの波の数)、`seed` を変えると模様が変わる。
    /// 岩・雲・幹など。
    /// 距離は厳密でなくなる (amp を freq に比べて大きくすると面が荒れる)。
    /// </summary>
    public SdfNode Displace(float amp, float freq, int octaves, int seed) =>
        Unary(Lub.Mesh.SdfOp.Displace,
            new List<float> { amp, freq, octaves, seed }, this);

    /// <summary>表面を `r` だけ外へ太らせて角を丸める (負なら削る)。
    /// </summary>
    public SdfNode Round(float r) =>
        Unary(Lub.Mesh.SdfOp.Round, new List<float> { r }, this);

    /// <summary>表面を厚さ `t` の殻にする (中は空洞、Subtract などで切ると見える)。
    /// </summary>
    public SdfNode Onion(float t) =>
        Unary(Lub.Mesh.SdfOp.Onion, new List<float> { t }, this);

    /// <summary>サブツリーに材質 (albedo + metallic/roughness) を与える。
    /// innermost の paint が勝つ。
    /// `rgb` は 0xRRGGBB (metallic 省略で 0.0、roughness 省略で 0.8)。
    /// smin/ssub では距離と同じ blend で材質も混ざり、subtract/ssub の切断面には cutter の材質が出る。
    /// </summary>
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

    /// <summary>smooth union。
    /// `k` が blend 幅。
    /// </summary>
    public SdfNode Smin(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Smin, new List<float> { k }, this, b);

    /// <summary>`b` をくり抜く。
    /// </summary>
    public SdfNode Subtract(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Subtract, new List<float>(), this, b);

    /// <summary>smooth subtraction。
    /// `k` が縁の丸まり幅。
    /// </summary>
    public SdfNode Ssub(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Ssub, new List<float> { k }, this, b);

    public SdfNode Intersect(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Intersect, new List<float>(), this, b);

    /// <summary>smooth intersection。
    /// `k` が縁の丸まり幅。
    /// </summary>
    public SdfNode Smax(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Smax, new List<float> { k }, this, b);

    /// <summary>和の継ぎ目を幅 `r` の 45 度の面取りにする (hg_sdf の opUnionChamfer)。
    /// </summary>
    public SdfNode UnionChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.UnionChamfer, new List<float> { r }, this, b);

    /// <summary>共通部分の角を幅 `r` で面取りする。
    /// </summary>
    public SdfNode IntersectChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.IntersectChamfer, new List<float> { r }, this, b);

    /// <summary>`b` をくり抜いた縁を幅 `r` で面取りする。
    /// </summary>
    public SdfNode SubtractChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.SubtractChamfer, new List<float> { r }, this, b);

    /// <summary>和の継ぎ目を幅 `r` の `n` 段の階段にする。
    /// </summary>
    public SdfNode UnionStairs(SdfNode b, float r, int n) =>
        Binary(Lub.Mesh.SdfOp.UnionStairs, new List<float> { r, n }, this, b);

    /// <summary>`b` の表面に沿って、自分に深さ `ra`、半幅 `rb` の溝を彫る。
    /// </summary>
    public SdfNode Groove(SdfNode b, float ra, float rb) =>
        Binary(Lub.Mesh.SdfOp.Groove, new List<float> { ra, rb }, this, b);

    /// <summary>`b` の表面に沿って、自分から高さ `ra`、半幅 `rb` の帯を盛り上げる (Groove の逆)。
    /// </summary>
    public SdfNode Tongue(SdfNode b, float ra, float rb) =>
        Binary(Lub.Mesh.SdfOp.Tongue, new List<float> { ra, rb }, this, b);

    /// <summary>自分と `b` の表面が交わる線に沿った、半径 `r` の管。
    /// </summary>
    public SdfNode Pipe(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.Pipe, new List<float> { r }, this, b);

    /// <summary>`b` の表面に沿って、自分に深さ `r` の V 字の溝を刻む。
    /// </summary>
    public SdfNode Engrave(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.Engrave, new List<float> { r }, this, b);

    /// <summary>どちらか一方だけに含まれる部分 (重なりをくり抜いた和)。
    /// </summary>
    public SdfNode Xor(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Xor, new List<float>(), this, b);

    /// <summary>サブツリーを skinning 部位として宣言する。
    /// `pivot` は関節位置 (model 空間)。
    /// メッシュ化時に頂点ごとの部位距離から重みが焼かれる。
    /// mirror_x の内側に置くと両側に重みが付くのに pivot が片側になるので、動かす bone は mirror の外で個別に置くこと。
    /// </summary>
    public SdfNode Bone(string name, Vec3 pivot)
    {
        var n = Unary(Lub.Mesh.SdfOp.Bone,
            new List<float> { pivot.X, pivot.Y, pivot.Z }, this);
        n.Name = name;
        return n;
    }
}

/// <summary>SDF モデリングの builder。
/// プリミティブを作り、`SdfNode` のメソッドチェーンで変形・合成し、`Sdf.Mesh` でメッシュ化する:
/// <code>
/// var body = Sdf.Sphere(0.72).Move(0, -0.42, 0);
/// var head = Sdf.Sphere(0.46).Move(0, 0.48, 0);
/// var mesh = Sdf.Mesh(body.Smin(head, 0.22), 64);
/// </code></summary>
public static class Sdf
{
    public static SdfNode Sphere(float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Sphere, new List<float> { r });

    /// <summary>half extents の直方体。
    /// </summary>
    public static SdfNode Box(float hx, float hy, float hz) =>
        new SdfNode(Lub.Mesh.SdfOp.Box, new List<float> { hx, hy, hz });

    /// <summary>線分 `a`-`b` を軸とする半径 `r` のカプセル。
    /// </summary>
    public static SdfNode Capsule(Vec3 a, Vec3 b, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Capsule,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r });

    /// <summary>線分 `a`-`b` を軸とし、半径が `a` 側の `ra` から `b` 側の `rb` へ線形に変わるカプセル (2 つの球の凸包)。
    /// 先細りの角・尾・指などに使う。
    /// 端の半径をそろえて折れ線に並べると、継ぎ目に段差の出ないなめらかな管になる。
    /// </summary>
    public static SdfNode RoundCone(Vec3 a, Vec3 b, float ra, float rb) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundCone,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, ra, rb });

    /// <summary>XZ 平面に寝たトーラス。
    /// </summary>
    public static SdfNode Torus(float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.Torus, new List<float> { rMajor, rMinor });

    /// <summary>half extents の直方体の辺と角を半径 `r` で丸めたもの (外形は half extents のまま、`r` は 0 以上で half extents 以下)。
    /// </summary>
    public static SdfNode RoundBox(float hx, float hy, float hz, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundBox, new List<float> { hx, hy, hz, r });

    /// <summary>half extents の直方体の 12 本の辺だけを、太さ (半幅) `e` の角棒で残した枠。
    /// </summary>
    public static SdfNode BoxFrame(float hx, float hy, float hz, float e) =>
        new SdfNode(Lub.Mesh.SdfOp.BoxFrame, new List<float> { hx, hy, hz, e });

    /// <summary>各軸の半径が `rx` / `ry` / `rz` の楕円体。
    /// 距離は近似 (面の位置は正確)。
    /// </summary>
    public static SdfNode Ellipsoid(float rx, float ry, float rz) =>
        new SdfNode(Lub.Mesh.SdfOp.Ellipsoid, new List<float> { rx, ry, rz });

    /// <summary>線分 `a`-`b` を軸とする半径 `r` の、端が平らな円柱。
    /// </summary>
    public static SdfNode CappedCylinder(Vec3 a, Vec3 b, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedCylinder,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r });

    /// <summary>線分 `a`-`b` を軸とし、半径が `a` 側の `ra` から `b` 側の `rb` へ変わる、端が平らな円錐台 (`rb` = 0 で円錐)。
    /// </summary>
    public static SdfNode CappedCone(Vec3 a, Vec3 b, float ra, float rb) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedCone,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, ra, rb });

    /// <summary>Y 軸に沿った半径 `r`、半分の高さ `h` の円柱の、上下の縁を半径 `e` で丸めたもの (外形は r と h のまま)。
    /// </summary>
    public static SdfNode RoundedCylinder(float r, float h, float e) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundedCylinder, new List<float> { r, h, e });

    /// <summary>XY 平面 (正面向き) のトーラスの弧。
    /// +Y を中心に左右へ `angle` ラジアンずつ開く (π で全周)。
    /// 笑った口などは Z 軸回りに π 回して下向きにする。
    /// </summary>
    public static SdfNode CappedTorus(float angle, float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedTorus,
            new List<float> { angle, rMajor, rMinor });

    /// <summary>鎖の輪: XY 平面の輪 (中心線の半径 `rMajor`、太さ `rMinor`) を Y 方向に `le` ずつ引き伸ばしたもの。
    /// </summary>
    public static SdfNode Link(float le, float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.Link, new List<float> { le, rMajor, rMinor });

    /// <summary>Z 軸に沿った六角柱。
    /// `h` は中心から上下の平らな辺までの距離、`depth` は奥行きの半分。
    /// </summary>
    public static SdfNode HexPrism(float h, float depth) =>
        new SdfNode(Lub.Mesh.SdfOp.HexPrism, new List<float> { h, depth });

    /// <summary>Z 軸に沿った正三角柱 (頂点が上)。
    /// 底辺は y = -h/2、頂点は y = h、`depth` は奥行きの半分。
    /// 距離は近似 (面の位置は正確)。
    /// </summary>
    public static SdfNode TriPrism(float h, float depth) =>
        new SdfNode(Lub.Mesh.SdfOp.TriPrism, new List<float> { h, depth });

    /// <summary>中心から 6 つの頂点までが `s` の正八面体。
    /// </summary>
    public static SdfNode Octahedron(float s) =>
        new SdfNode(Lub.Mesh.SdfOp.Octahedron, new List<float> { s });

    /// <summary>四角錐: 底面は y = 0 の半幅 `w` の正方形、頂点は (0, `h`, 0)。
    /// </summary>
    public static SdfNode Pyramid(float w, float h) =>
        new SdfNode(Lub.Mesh.SdfOp.Pyramid, new List<float> { w, h });

    /// <summary>半径 `r` の球のうち、平面 y = `h` より上の部分 (-r &lt; h &lt; r)。
    /// </summary>
    public static SdfNode CutSphere(float r, float h) =>
        new SdfNode(Lub.Mesh.SdfOp.CutSphere, new List<float> { r, h });

    /// <summary>半径 `r` の球のうち、+Y 軸から `angle` ラジアン以内の扇形 (原点が頂点の円錐に球の蓋)。
    /// </summary>
    public static SdfNode SolidAngle(float angle, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.SolidAngle, new List<float> { angle, r });

    /// <summary>XZ 平面の菱形 (対角線の半分が x に `la`、z に `lb`) を角の半径 `r` で丸め、Y 方向に半分の厚さ `h` で押し出したもの。
    /// </summary>
    public static SdfNode Rhombus(float la, float lb, float h, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Rhombus, new List<float> { la, lb, h, r });

    /// <summary>`a` と `b` を尖った両端とし、中央の半径が `w` のレンズ (2 本の円弧を軸回りに回した形)。
    /// 葉・花びら・目などに使う。
    /// </summary>
    public static SdfNode Vesica(Vec3 a, Vec3 b, float w) =>
        new SdfNode(Lub.Mesh.SdfOp.Vesica,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, w });

    /// <summary>op の表示名 (SdfPanel のラベル用)。
    /// </summary>
    public static string OpName(Lub.Mesh.SdfOp op)
    {
        switch (op)
        {
            case Lub.Mesh.SdfOp.Sphere: return "sphere";
            case Lub.Mesh.SdfOp.Box: return "box";
            case Lub.Mesh.SdfOp.Capsule: return "capsule";
            case Lub.Mesh.SdfOp.Torus: return "torus";
            case Lub.Mesh.SdfOp.RoundCone: return "round_cone";
            case Lub.Mesh.SdfOp.RoundBox: return "round_box";
            case Lub.Mesh.SdfOp.BoxFrame: return "box_frame";
            case Lub.Mesh.SdfOp.Ellipsoid: return "ellipsoid";
            case Lub.Mesh.SdfOp.CappedCylinder: return "capped_cylinder";
            case Lub.Mesh.SdfOp.CappedCone: return "capped_cone";
            case Lub.Mesh.SdfOp.RoundedCylinder: return "rounded_cylinder";
            case Lub.Mesh.SdfOp.CappedTorus: return "capped_torus";
            case Lub.Mesh.SdfOp.Link: return "link";
            case Lub.Mesh.SdfOp.HexPrism: return "hex_prism";
            case Lub.Mesh.SdfOp.TriPrism: return "tri_prism";
            case Lub.Mesh.SdfOp.Octahedron: return "octahedron";
            case Lub.Mesh.SdfOp.Pyramid: return "pyramid";
            case Lub.Mesh.SdfOp.CutSphere: return "cut_sphere";
            case Lub.Mesh.SdfOp.SolidAngle: return "solid_angle";
            case Lub.Mesh.SdfOp.Rhombus: return "rhombus";
            case Lub.Mesh.SdfOp.Vesica: return "vesica";
            case Lub.Mesh.SdfOp.Move: return "move";
            case Lub.Mesh.SdfOp.Rotate: return "rotate";
            case Lub.Mesh.SdfOp.Scale: return "scale";
            case Lub.Mesh.SdfOp.MirrorX: return "mirror_x";
            case Lub.Mesh.SdfOp.MirrorY: return "mirror_y";
            case Lub.Mesh.SdfOp.MirrorZ: return "mirror_z";
            case Lub.Mesh.SdfOp.Elongate: return "elongate";
            case Lub.Mesh.SdfOp.Twist: return "twist";
            case Lub.Mesh.SdfOp.Bend: return "bend";
            case Lub.Mesh.SdfOp.Repeat: return "repeat";
            case Lub.Mesh.SdfOp.PolarRepeat: return "polar_repeat";
            case Lub.Mesh.SdfOp.Revolve: return "revolve";
            case Lub.Mesh.SdfOp.Extrude: return "extrude";
            case Lub.Mesh.SdfOp.Displace: return "displace";
            case Lub.Mesh.SdfOp.Round: return "round";
            case Lub.Mesh.SdfOp.Onion: return "onion";
            case Lub.Mesh.SdfOp.Paint: return "paint";
            case Lub.Mesh.SdfOp.Bone: return "bone";
            case Lub.Mesh.SdfOp.Union: return "union";
            case Lub.Mesh.SdfOp.Smin: return "smin";
            case Lub.Mesh.SdfOp.Subtract: return "subtract";
            case Lub.Mesh.SdfOp.Ssub: return "ssub";
            case Lub.Mesh.SdfOp.Intersect: return "intersect";
            case Lub.Mesh.SdfOp.Smax: return "smax";
            case Lub.Mesh.SdfOp.Xor: return "xor";
            case Lub.Mesh.SdfOp.UnionChamfer: return "union_chamfer";
            case Lub.Mesh.SdfOp.IntersectChamfer: return "intersect_chamfer";
            case Lub.Mesh.SdfOp.SubtractChamfer: return "subtract_chamfer";
            case Lub.Mesh.SdfOp.UnionStairs: return "union_stairs";
            case Lub.Mesh.SdfOp.Groove: return "groove";
            case Lub.Mesh.SdfOp.Tongue: return "tongue";
            case Lub.Mesh.SdfOp.Pipe: return "pipe";
            case Lub.Mesh.SdfOp.Engrave: return "engrave";
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

    /// <summary>ツリーをメッシュ化する。
    /// `n` は最長軸の cell 数 (bounds は自動)。
    /// bone ノードがあれば skinning 情報も焼かれる (`skinK` = 重みの blend 幅)。
    /// </summary>
    public static MeshData Mesh(SdfNode root, int n, float? skinK = null)
    {
        var nodes = new List<SdfNodeDesc>();
        var r = Flatten(root, nodes);
        return Lub.Mesh.SdfMesh(nodes, r, n, skinK);
    }
}

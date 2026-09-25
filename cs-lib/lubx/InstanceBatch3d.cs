// 実装ライブラリ lubx の InstanceBatch3d。
// instance の列は List<float> に添字で書く。List は伸ばすだけで縮めず、
// 足りなくなったときだけ倍に伸ばす (List.Clear() は Lua でクロージャを作り、
// Add を毎回呼ぶと table.insert になるので、どちらも毎フレームは使わない)。
// Upload() は先頭の Count 個分だけを TransientBuffer に写す。

using System.Collections.Generic;
using static Lub;

/// <summary>
/// 同じメッシュを位置・回転・大きさ・色を変えてたくさん描くための instance の列。
/// 毎フレーム Begin() で空にし、Add() / AddScaled() で 1 つずつ積んで、
/// Renderer3d.DrawInstances() に渡す。何個でも積め、積むときに確保はしない
/// (列が足りなくなったときだけ伸ばす)。
///
/// <code>
/// var coins = new InstanceBatch3d();
/// // 毎フレーム:
/// coins.Begin();
/// foreach (var c in live)
///     coins.AddScaled(c.X, c.Y, c.Z, 0.5f, 0.1f, 0.5f,
///         c.Qx, c.Qy, c.Qz, c.Qw, 1.0f, 0.82f, 0.25f, 1.0f);
/// ren.DrawInstances(coinMesh, coins);
/// </code>
///
/// 1 instance は Stride (16) 個の float で、shader からは次の struct に見える:
/// <code>
/// struct Inst {
///   float3 pos;   // world の位置
///   float pad0;
///   float3 scale; // 軸ごとの拡大 (メッシュのローカル軸)
///   float pad1;
///   float4 rot;   // 回転の quaternion (x, y, z, w)。正規化済み
///   float4 color; // 頂点色に乗じる色 (sRGB)。a は不透明度
/// };
/// </code>
/// 頂点は拡大 → 回転 → 平行移動の順に置く (Mat4.SetTrs と同じ)。
/// 自分の shader で使うときは Upload() の BufferRef を `insts` に束縛し、
/// `LUB_INSTANCE_ID` で引いて、DrawOpts.InstanceCount に Count を渡す。
/// </summary>
public class InstanceBatch3d
{
    /// <summary>1 instance の float 数。</summary>
    public const int Stride = 16;

    /// <summary>積んだ instance の数。</summary>
    public int Count { get; private set; }

    private List<float> data = new List<float>();
    private int capacity = 0;

    /// <summary>列を空にする。確保した分はそのまま次に使う。</summary>
    public void Begin()
    {
        Count = 0;
    }

    /// <summary>3 軸同じ大きさの instance を 1 つ積む。(x, y, z) は位置、
    /// (qx, qy, qz, qw) は正規化した回転の quaternion、(r, g, b, a) は色。</summary>
    public void Add(float x, float y, float z, float scale, float qx,
        float qy, float qz, float qw, float r, float g, float b, float a)
    {
        int o = Count * Stride;
        if (o + Stride > capacity)
            Grow(o + Stride);
        var d = data;
        d[o] = x;
        d[o + 1] = y;
        d[o + 2] = z;
        d[o + 3] = 0.0f;
        d[o + 4] = scale;
        d[o + 5] = scale;
        d[o + 6] = scale;
        d[o + 7] = 0.0f;
        d[o + 8] = qx;
        d[o + 9] = qy;
        d[o + 10] = qz;
        d[o + 11] = qw;
        d[o + 12] = r;
        d[o + 13] = g;
        d[o + 14] = b;
        d[o + 15] = a;
        Count = Count + 1;
    }

    /// <summary>軸ごとに大きさの違う instance を 1 つ積む。(sx, sy, sz) は
    /// メッシュのローカル軸ごとの拡大で、ほかは Add と同じ。</summary>
    public void AddScaled(float x, float y, float z, float sx, float sy,
        float sz, float qx, float qy, float qz, float qw, float r, float g,
        float b, float a)
    {
        int o = Count * Stride;
        if (o + Stride > capacity)
            Grow(o + Stride);
        var d = data;
        d[o] = x;
        d[o + 1] = y;
        d[o + 2] = z;
        d[o + 3] = 0.0f;
        d[o + 4] = sx;
        d[o + 5] = sy;
        d[o + 6] = sz;
        d[o + 7] = 0.0f;
        d[o + 8] = qx;
        d[o + 9] = qy;
        d[o + 10] = qz;
        d[o + 11] = qw;
        d[o + 12] = r;
        d[o + 13] = g;
        d[o + 14] = b;
        d[o + 15] = a;
        Count = Count + 1;
    }

    // 列を need 個以上に伸ばす。伸ばすたびに倍にするので、伸ばす回数は
    // 最大の個数に対して log 回で済む。
    private void Grow(int need)
    {
        int cap = capacity * 2;
        if (cap < need)
            cap = need;
        while (data.Count < cap)
            data.Add(0.0f);
        capacity = cap;
    }

    /// <summary>
    /// 今の中身 (先頭の Count 個) を TransientBuffer に写し、その BufferRef を
    /// 返す。Count が 0 なら何も作らずに null。呼ぶたびに写すので、1 フレームに
    /// 1 回呼んで、返った BufferRef を複数の pass で使い回す。BufferRef は
    /// 作ったフレームの間だけ使える。
    /// </summary>
    public BufferRef? Upload()
    {
        if (Count == 0)
            return null;
        return Gfx.TransientBuffer(Gfx.BufferType.Storage, data, Count * Stride);
    }
}

// 実装ライブラリ lubx の XrAnchor。
// Xr.View が返す視点は OpenXR / WebXR の LOCAL 空間 (起動時の頭の位置が原点、
// メートル、前方 -Z)。ゲームは自分の空間に置きたいので、基準にする頭の位置と
// 水平方向の向きを取り、以後の眼の行列に掛けて写す。runtime は状態を持たず、
// どの frame を基準にするかはゲームが決める。行列は XrView.ViewProjection と
// 同じ行優先の float[16] で受け渡す。

using System;
using Lub;

namespace Lubx;

/// <summary>XR の視点を、Recenter した時点の両眼の中点と水平方向の向きを
/// 原点・前方 -Z とする空間へ写す。毎フレーム ViewProjection に XrView を
/// 渡す。Recenter するまでは LOCAL 空間のまま。</summary>
public class XrAnchor
{
    readonly float[] matrix = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    bool set;

    /// <summary>Recenter 済みか。</summary>
    public bool IsSet() => set;

    /// <summary>両眼の中点と左眼の水平方向の向きを基準に取る。</summary>
    public void Recenter(XrView left, XrView right)
    {
        var o = left.Orientation;
        var p = left.Position;
        var q = right.Position;
        var yaw = (float)Math.Atan2(2 * (o[0] * o[2] + o[1] * o[3]),
            1 - 2 * (o[0] * o[0] + o[1] * o[1]));
        var c = (float)Math.Cos(yaw);
        var s = (float)Math.Sin(yaw);
        for (var i = 0; i < 16; i++)
        {
            matrix[i] = 0;
        }
        matrix[0] = c;
        matrix[2] = s;
        matrix[5] = 1;
        matrix[8] = -s;
        matrix[10] = c;
        matrix[15] = 1;
        matrix[3] = (p[0] + q[0]) * 0.5f;
        matrix[7] = (p[1] + q[1]) * 0.5f;
        matrix[11] = (p[2] + q[2]) * 0.5f;
        set = true;
    }

    /// <summary>基準を捨てて LOCAL 空間に戻す。</summary>
    public void Reset()
    {
        for (var i = 0; i < 16; i++)
        {
            matrix[i] = i % 5 == 0 ? 1 : 0;
        }
        set = false;
    }

    /// <summary>基準の空間から LOCAL 空間への行列 (行優先、平行移動は [3] [7] [11])。</summary>
    public float[] ToLocal() => matrix;

    /// <summary>view の projection * view に基準を掛けた行列を result (16 要素) へ
    /// 書く。行優先で、基準の空間の座標をそのまま描ける。</summary>
    public void ViewProjection(XrView view, float[] result)
    {
        var vp = view.ViewProjection;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                result[row * 4 + col] = vp[row * 4] * matrix[col]
                    + vp[row * 4 + 1] * matrix[4 + col]
                    + vp[row * 4 + 2] * matrix[8 + col]
                    + vp[row * 4 + 3] * matrix[12 + col];
            }
        }
    }
}

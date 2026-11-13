// lider の空間: 見る人を原点に、コードを半径 R の円筒の面に並べる。
// 座標は lub の 3D ワールドと同じ左手系 (+X 右、+Y 上、+Z 前方)、単位はメートル。
// XR の視点 (右手系、前方 -Z) へは Z を反転して写す。
// 非 XR では頭を回して読めないので、円筒を平面 z = r に開く。角度 a は x = r * a になる。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class Space
{
    // 円筒の半径 (読む面を置く距離)。
    // TODO: Frame のピントの距離を測って決める
    public const float R = 1.6f;

    // 円筒を平面に開いているか (XR でないとき)
    public static bool Flat;
    // 平面で開く窓の大きさ。既定の画角で、窓の高さが画面の 9 割になる
    public const float FlatScale = 1.5f;
    // 非 XR の画角の既定 (度)
    public const float FlatFov = 50;

    // 角度 a、高さ y、半径 r の面の上の点
    public static Vec3 At(float a, float y, float r)
    {
        return Flat ? new Vec3(r * a, y, r) : new Vec3(r * (float)Math.Sin(a), y, r * (float)Math.Cos(a));
    }

    // 角度 a の面の向き (Y 軸の回りの角度)
    public static float Facing(float a)
    {
        return Flat ? 0 : a;
    }

    // 円筒の角度 a (右が正、ラジアン) と高さ y の位置に、幅 w 高さ h の板を半径 r で置く model 行列
    public static Mat4 OnCylinder(float a, float y, float r, float w, float h)
    {
        return Mat4.Translate(At(a, y, r)) * Mat4.RotateY(Facing(a)) * Mat4.Scale(new Vec3(w, h, 1));
    }

    // 円筒上の板を ray で指したときの板の中の位置 (左上 0,0、右下 1,1)。
    // 当たらなければ null
    public static Vec2? Hit(Ray ray, float a, float y, float r, float w, float h)
    {
        var center = At(a, y, r);
        var face = Facing(a);
        var normal = new Vec3(-(float)Math.Sin(face), 0, -(float)Math.Cos(face));
        var t = ray.IntersectPlane(center, normal);
        if (t == null)
            return null;
        var d = ray.At(t.Value) - center;
        var right = new Vec3((float)Math.Cos(face), 0, -(float)Math.Sin(face));
        var u = d.Dot(right) / w + 0.5f;
        var v = 0.5f - d.Y / h;
        if (u < 0 || u > 1 || v < 0 || v > 1)
            return null;
        return new Vec2(u, v);
    }

    // 円筒 (半径 r) に沿って曲げた板を ray で指したときの板の中の位置 (左上 0,0、右下 1,1)。
    // 板は角度 angle、高さ y を中心に、弧の長さ w、高さ h。
    // ray は円筒の内側から出る (見る人は原点の近く)。
    // 当たらなければ null
    public static Vec2? HitCurved(Ray ray, float angle, float y, float r, float w, float h)
    {
        var p = OnCylinderAt(ray, r);
        if (p == null)
            return null;
        var da = p.Angle - angle;
        while (!Flat && da > Math.PI)
            da -= 2 * (float)Math.PI;
        while (!Flat && da < -Math.PI)
            da += 2 * (float)Math.PI;
        var u = da / (w / r) + 0.5f;
        var v = 0.5f - (p.Y - y) / h;
        if (u < 0 || u > 1 || v < 0 || v > 1)
            return null;
        return new Vec2(u, v);
    }

    // 平面で視点の x を view に置くとき、中心 center、幅の半分 half の窓が画面 (幅の半分 viewport) に入る、一番近い view。
    // 窓が画面より広ければ窓の真ん中を見る
    public static float FollowX(float view, float center, float half, float viewport)
    {
        if (half >= viewport)
            return center;
        if (center - half < view - viewport)
            return center - half + viewport;
        if (center + half > view + viewport)
            return center + half - viewport;
        return view;
    }

    // 見ている角度 gaze から、角度 target の窓 (幅の半分の角度 half) が視野 (半分の角度 fov) の左 (-1) か右 (1) に外れているか。入っていれば 0
    public static int OffView(float gaze, float target, float half, float fov)
    {
        var d = target - gaze;
        while (d > Math.PI)
            d -= 2 * (float)Math.PI;
        while (d < -Math.PI)
            d += 2 * (float)Math.PI;
        if (Math.Abs(d) <= fov + half)
            return 0;
        return d > 0 ? 1 : -1;
    }

    // ray が半径 r の円筒に内側から当たる点の角度と高さ (当たらなければ null)
    public static CylinderPoint? OnCylinderAt(Ray ray, float r)
    {
        var o = ray.Origin;
        var d = ray.Dir;
        if (Flat)
        {
            if (d.Z < 1e-5f)
                return null;
            var hit = ray.At((r - o.Z) / d.Z);
            return new CylinderPoint(hit.X / r, hit.Y);
        }
        var qa = d.X * d.X + d.Z * d.Z;
        if (qa < 1e-8f)
            return null;
        var qb = 2 * (o.X * d.X + o.Z * d.Z);
        var qc = o.X * o.X + o.Z * o.Z - r * r;
        var disc = qb * qb - 4 * qa * qc;
        if (disc < 0)
            return null;
        var t = (-qb + (float)Math.Sqrt(disc)) / (2 * qa);
        if (t < 0)
            return null;
        var p = ray.At(t);
        return new CylinderPoint((float)Math.Atan2(p.X, p.Z), p.Y);
    }

    // XR の眼の行列 (anchor の空間、右手系) を lider の空間で使える形にする
    public static Mat4 FromXr(float[] viewProjection)
    {
        var values = new List<float>();
        for (var i = 0; i < 16; i++)
            values.Add(viewProjection[i]);
        var m = Mat4.FromList(values, 0);
        return m * Mat4.Scale(new Vec3(1, 1, -1));
    }
}

// ray が当たった窓と、その中の位置 (左上 0,0、右下 1,1)
public class WindowHit
{
    public Window Window;
    public Vec2 Uv;

    public WindowHit(Window window, Vec2 uv)
    {
        Window = window;
        Uv = uv;
    }
}

// 円筒の上の点 (角度と高さ)
public class CylinderPoint
{
    public float Angle;
    public float Y;

    public CylinderPoint(float angle, float y)
    {
        Angle = angle;
        Y = y;
    }
}

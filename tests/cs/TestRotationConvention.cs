// 回転の規約がスタック全体で 1 つであることを検査する:
// Quat.RotateVec3、Quat.ToMat4、Mat4.Rotate、Mat4.RotateX/Y/Z、そして Phys3d (box3d) が pose の quaternion で回すローカル点は全部同じ写像。
// 期待値は Rodrigues の式で独立に計算する。
using System;
using Lub;
using Lubx;

public static class TestRotationConvention
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static void Near(Vec3 a, Vec3 b, string label)
    {
        var ok = Math.Abs(a.X - b.X) < 1e-5f && Math.Abs(a.Y - b.Y) < 1e-5f && Math.Abs(a.Z - b.Z) < 1e-5f;
        Check.That(ok, label + ": got (" + a.X + ", " + a.Y + ", " + a.Z + ") expected (" + b.X + ", " + b.Y + ", " + b.Z + ")");
    }

    static void NearQuat(Quat a, Quat b, string label)
    {
        // q と -q は同じ回転
        var sign = (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W) < 0 ? -1f : 1f;
        var ok = Math.Abs(a.X - sign * b.X) < 1e-5f
            && Math.Abs(a.Y - sign * b.Y) < 1e-5f
            && Math.Abs(a.Z - sign * b.Z) < 1e-5f
            && Math.Abs(a.W - sign * b.W) < 1e-5f;
        Check.That(ok, label);
    }

    // 右ねじの能動回転 (Rodrigues)。
    // axis は正規化済み。
    static Vec3 Rodrigues(Vec3 axis, float angle, Vec3 p)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var dot = axis.X * p.X + axis.Y * p.Y + axis.Z * p.Z;
        return new Vec3(
            p.X * c + (axis.Y * p.Z - axis.Z * p.Y) * s + axis.X * dot * (1 - c),
            p.Y * c + (axis.Z * p.X - axis.X * p.Z) * s + axis.Y * dot * (1 - c),
            p.Z * c + (axis.X * p.Y - axis.Y * p.X) * s + axis.Z * dot * (1 - c));
    }

    static Mat4 Principal(int i, float angle)
    {
        if (i == 0)
            return Mat4.RotateX(angle);
        if (i == 1)
            return Mat4.RotateY(angle);
        return Mat4.RotateZ(angle);
    }

    static void CheckMath()
    {
        var x = new Vec3(1, 0, 0);
        var y = new Vec3(0, 1, 0);
        var z = new Vec3(0, 0, 1);
        // 規約の定義そのもの: +Z 回りの +90 度は +X を +Y に、+Y 回りの +90 度は +Z を +X に回す
        Near(Quat.FromAxisAngle(z, (float)Math.PI / 2).RotateVec3(x), y, "Z +90deg maps +X to +Y");
        Near(Mat4.RotateZ((float)Math.PI / 2).MulDir(x), y, "Mat4.RotateZ +90deg maps +X to +Y");
        Near(Mat4.RotateY((float)Math.PI / 2).MulDir(z), x, "Mat4.RotateY +90deg maps +Z to +X");
        Near(Mat4.RotateX((float)Math.PI / 2).MulDir(y), z, "Mat4.RotateX +90deg maps +Y to +Z");

        var p = new Vec3(0.7f, -0.4f, 0.3f);
        var axes = new Vec3[] { x, y, z, new Vec3(1, 2, 3).Normalize() };
        var angles = new float[] { (float)Math.PI / 2, -0.7f, 0, 2.5f };
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            for (int j = 0; j < angles.Length; j++)
            {
                var angle = angles[j];
                var expected = Rodrigues(axis, angle, p);
                var q = Quat.FromAxisAngle(axis, angle);
                Near(q.RotateVec3(p), expected, "Quat.RotateVec3");
                Near(q.ToMat4().MulDir(p), expected, "Quat.ToMat4");
                Near(Mat4.Rotate(angle, axis).MulDir(p), expected, "Mat4.Rotate");
                Near(Mat4.FromQuat(q).MulDir(p), expected, "Mat4.FromQuat");
                if (i < 3)
                    Near(Principal(i, angle).MulDir(p), expected, "Mat4.RotateX/Y/Z");
                NearQuat(Quat.FromMat4(q.ToMat4()), q, "Quat.FromMat4 inverts ToMat4");
            }
        }

        // 合成: a.Mul(b) は b を先に適用。
        // 行列の積と一致する
        var a = Quat.FromAxisAngle(axes[3], 1.1f);
        var b = Quat.FromAxisAngle(y, -0.6f);
        Near(a.Mul(b).RotateVec3(p), a.RotateVec3(b.RotateVec3(p)), "Quat.Mul applies b first");
        Near(a.Mul(b).ToMat4().MulDir(p), a.ToMat4().Mul(b.ToMat4()).MulDir(p), "Quat.Mul matches Mat4.Mul");
        // FromEuler: roll (X) → pitch (Y) → yaw (Z) の順に適用
        var e = Quat.FromEuler(0.4f, -0.3f, 0.9f);
        var composed = Quat.FromAxisAngle(z, 0.4f).Mul(Quat.FromAxisAngle(y, -0.3f)).Mul(Quat.FromAxisAngle(x, 0.9f));
        NearQuat(e, composed, "Quat.FromEuler order");
    }

    // 物理 (box3d) が pose の quaternion で回した collider の位置と、PoseMat で描画側が置く頂点位置が一致する。
    // raycast で実 collider を当てて確かめる。
    static void CheckPhysics()
    {
        var p = new Vec3(0.7f, -0.4f, 0.3f);
        var axes = new Vec3[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 2, 3).Normalize() };
        var angles = new float[] { (float)Math.PI / 2, -0.7f, 0 };
        for (int i = 0; i < axes.Length; i++)
        {
            for (int j = 0; j < angles.Length; j++)
            {
                var expected = Rodrigues(axes[i], angles[j], p) + new Vec3(2, 3, 4);
                var q = Quat.FromAxisAngle(axes[i], angles[j]);
                var w = Phys3d.World("rotation_convention:" + (i + 1) + ":" + (j + 1), new WorldOpts3d { Gravity = new Vec3d { X = 0, Y = 0, Z = 0 } });
                Phys3d.Begin(w!);
                var body = Phys3d.Body(w!, "body", new BodyDesc3d
                {
                    Type = Phys3d.BodyType.Static,
                    Initial = new InitialState3d { X = 2, Y = 3, Z = 4, Quat = new Quat3d { X = q.X, Y = q.Y, Z = q.Z, W = q.W } },
                });
                Phys3d.Sphere(body!, "offset", new SphereDesc3d { R = 0.05f, Offset = new Vec3d { X = p.X, Y = p.Y, Z = p.Z } });
                Phys3d.Step(w!, 1.0f / 60.0f);
                var pose = Phys3d.Pose(body!);
                Check.That(pose != null, "pose missing");
                if (pose == null)
                    return;
                Near(Renderer3d.PoseMat(pose).MulPoint(p), expected, "PoseMat differs from physics rotation");
                var hit = Phys3d.Raycast(w!, new RaycastDesc3d { X = expected.X, Y = expected.Y, Z = expected.Z - 0.5f, Dx = 0, Dy = 0, Dz = 1 });
                Check.That(hit != null, "expected point is not at the real collider");
            }
        }
    }

    public static void OnFrame(float dt)
    {
        CheckMath();
        CheckPhysics();
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

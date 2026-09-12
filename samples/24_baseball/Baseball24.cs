// lub の samples/24_baseball の entry。
// 実行: lub samples/24_baseball/Baseball24.csproj (transpile + watch + hot reload)
//
// 全自動野球シミュレーション。ユーザーは観るだけ。
// - キャラ: SDF モデリング (bone 付き) + vertex shader LBS。1 体のメッシュを
//   ボーン行列とチーム色 uniform で全員に使い回す
// - 物理: 手書き弾道 (重力 + 空気抵抗 + バウンド + フェンス反射)。
//   球 vs 平面/円筒の解析判定なのですり抜けしない
// - 試合: state machine で投球→打撃→守備→走塁を全自動進行。捕球・封殺は
//   野手と走者の実際の位置と時間で決まる (結果の先取りをしない)
// - 演出: 投打を固定構図で見せ、打球後は守備と走塁を同じ画面に収める
//
// boneSlot 表 + 手詰めの packBones は cs-lib の Bones.pack (mesh.bones 順の
// resolve callback) に置き換え、乱数は Math.random でなく決定的な Rand。
// cs-lib のクラスは生成 Lua でサンプルより後に定義されるため static 初期化子
// では参照できず、reloaded ブロックで遅延生成する (Iroha21 と同じ扱い)。

using System;
using System.Collections.Generic;
using static Lub;

// 走者。塁 index は 0=本塁(打席) 1..3=各塁 4=生還
public class Runner
{
    public float X;
    public float Z;
    public int AtBase; // 今いる/直前の塁 (base は C# 予約語のため AtBase)
    public int To; // 目標の塁
    public float RunPhase = 0;

    public Runner(float x, float z, int atBase, int to)
    {
        this.X = x;
        this.Z = z;
        this.AtBase = atBase;
        this.To = to;
    }
}

// 野手。home* は定位置、(x,z) が現在地
public class Fielder
{
    public float X;
    public float Z;
    public float HomeX;
    public float HomeZ;
    public float Yaw = 0;
    public float RunPhase = 0;
    public int Anim = 0; // Baseball24.AN_*
    public float AnimT = 0;
    public BaseballPose? CatchPose;

    public Fielder(float hx, float hz)
    {
        HomeX = hx;
        HomeZ = hz;
        X = hx;
        Z = hz;
    }
}

// predictLanding の結果
public class Landing
{
    public float X;
    public float Z;
    public float T;
    public float Peak;
}

public class BaseballPose
{
    public float Twist, Lean, Tilt, Drop, Shift, HeadPitch, HeadYaw, BallTransfer;
    public float KneeSpread = 0.25f;
    public bool Bind;
    public Vec3 LeftHand = Baseball24.RestWrist(1);
    public Vec3 RightHand = Baseball24.RestWrist(-1);
    public Vec3 LeftFoot = Baseball24.RestAnkle(1);
    public Vec3 RightFoot = Baseball24.RestAnkle(-1);
}

public class BaseballRig
{
    public Dictionary<string, Mat4> Matrices = new Dictionary<string, Mat4>();
    public Vec3 LeftHand = new Vec3(0, 0, 0);
    public Vec3 RightHand = new Vec3(0, 0, 0);
    public Vec3 Ball = new Vec3(0, 0, 0);
}

public static class Baseball24
{
    const int w = 960;
    const int h = 540;
    const float tickDt = 1.0f / 60.0f;

    // --- フィールド寸法 (m) -------------------------------------------------
    const float baseD = 19.4f; // 塁間 27.43m の対角成分
    const float moundZ = 18.44f;
    const float fenceR = 76.0f;
    const float fenceH = 3.0f;
    const float grav = 9.8f;
    const float ballR = 0.115f;
    const float dragCoef = 0.0055f; // 打球の空気抵抗 (終端速度 ~42m/s)
    const float runSpd = 7.2f; // 走者/野手の走速
    const float catchR = 1.15f; // 捕球半径 (グラブの届く範囲)

    // --- 試合 state ---------------------------------------------------------
    const int stIntro = 0;
    const int stPrepitch = 1;
    const int stWindup = 2;
    const int stPitch = 3;
    const int stLive = 4;
    const int stCall = 5;
    const int stChange = 6;
    const int stEnd = 7;

    // キャラアニメ
    public const int AnIdle = 0;
    public const int AnReady = 1;
    public const int AnRun = 2;
    public const int AnWindup = 3;
    public const int AnSwing = 4;
    public const int AnReach = 5;
    public const int AnCrouch = 6;
    public const int AnThrow = 7;

    // 打球フェーズ (ST_LIVE 中)
    const int plFly = 0; // 打球が空中 (ノーバウンド)
    const int plTouch1b = 1;
    const int plThrow1b = 2; // 一塁送球中
    const int plSettle = 3; // 判定確定、走者が到達するのを待つ
    const int plFoul = 4;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });
    }

    public static void OnEvent(EventData e)
    {
        if (e.Key != 59) return; // F2 scancode
        if (e.Kind == EventKind.KeyUp) debugKeyHeld = false;
        if (e.Kind == EventKind.KeyDown && !debugKeyHeld)
        {
            debugKeyHeld = true;
            modelDebug = !modelDebug;
        }
    }

    public static void OnQuit()
    {
    }

    // --- 乱数 (決定的 xorshift。Math.random は run ごとに列が変わる) ---------
    static Rand? rng = null;

    static float Rnd()
    {
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x0B5EBA11);
            rng = r;
        }
        return r.NextFloat();
    }

    static float Rrange(float a, float b)
    {
        return a + (b - a) * Rnd();
    }

    // --- キャラメッシュ (SDF + bones) ---------------------------------------
    // 身長 ~1.8m。ユニフォームをチーム色で焼いた 2 メッシュを使い分ける
    static List<Mesh3d>? charMesh = null;
    static int[] teamRgb = new int[] { 0xD94038, 0x4073E0 };
    const float torsoPy = 0.95f;
    const float headPy = 1.50f;

    public static Vec3 RestShoulder(float side) => new Vec3(-side * 0.28f, 1.42f, 0);
    public static Vec3 RestElbow(float side) => new Vec3(-side * 0.42f, 1.15f, 0.03f);
    public static Vec3 RestWrist(float side) => new Vec3(-side * 0.55f, 0.88f, 0.06f);
    public static Vec3 RestHip(float side) => new Vec3(-side * 0.13f, 0.90f, 0);
    public static Vec3 RestKnee(float side) => new Vec3(-side * 0.14f, 0.51f, 0.03f);
    public static Vec3 RestAnkle(float side) => new Vec3(-side * 0.15f, 0.12f, 0);

    static SdfNode CharModel(int jersey)
    {
        var skin = 0xEFB68A;
        var ink = 0x172C43;
        var pants = 0xF5EDDA;
        var hips = Sdf.Capsule(new Vec3(-0.13f, 0.89f, 0), new Vec3(0.13f, 0.89f, 0), 0.145f)
            .Paint(pants).Bone("hips", new Vec3(0, 0.90f, 0));
        for (int side = -1; side <= 1; side += 2)
        {
            var suffix = side > 0 ? "_l" : "_r";
            var hip = RestHip(side);
            var knee = RestKnee(side);
            var ankle = RestAnkle(side);
            var thigh = Sdf.Capsule(hip, knee, 0.11f).Paint(pants).Bone("thigh" + suffix, hip);
            var shin = Sdf.Capsule(knee, ankle, 0.095f).Paint(pants)
                .Union(Sdf.Capsule(ankle + new Vec3(0, 0.02f, 0), ankle + new Vec3(0, 0.13f, 0), 0.097f).Paint(jersey))
                .Bone("shin" + suffix, knee);
            var foot = Sdf.Capsule(ankle + new Vec3(0, -0.035f, -0.025f), ankle + new Vec3(0, -0.035f, 0.14f), 0.085f)
                .Paint(ink).Bone("foot" + suffix, ankle);
            hips = hips.Smin(thigh, 0.02f).Smin(shin, 0.02f).Smin(foot, 0.015f);
        }
        var torso = Sdf.Capsule(new Vec3(0, 0.98f, 0), new Vec3(0, 1.35f, 0), 0.23f)
            .Intersect(Sdf.Box(0.4f, 0.4f, 0.4f).Move(0, 1.36f, 0))
            .Paint(jersey, 0, 0.7f)
            .Union(Sdf.Box(0.027f, 0.22f, 0.012f).Move(0, 1.20f, 0.226f).Paint(0xFFF1D7))
            .Union(Sdf.Box(0.23f, 0.045f, 0.18f).Move(0, 0.98f, 0).Paint(ink))
            .Bone("torso", new Vec3(0, torsoPy, 0));
        var head = Sdf.Sphere(0.225f).Move(0, 1.66f, 0).Paint(skin, 0, 0.65f)
            .Union(Sdf.Sphere(0.233f).Move(0, 1.73f, -0.018f)
                .Subtract(Sdf.Box(0.4f, 0.3f, 0.4f).Move(0, 1.38f, 0)).Paint(jersey, 0, 0.45f))
            .Union(Sdf.Box(0.21f, 0.028f, 0.14f).Move(0, 1.77f, 0.19f).Paint(jersey))
            .Union(Sdf.Sphere(0.030f).Move(0.084f, 1.66f, 0.202f).MirrorX().Paint(ink))
            .Union(Sdf.Sphere(0.047f).Move(0, 1.60f, 0.216f).Paint(skin))
            .Union(Sdf.Sphere(0.058f).Move(0.222f, 1.63f, 0).MirrorX().Paint(skin))
            .Bone("head", new Vec3(0, headPy, 0));
        var model = torso.Smin(head, 0.025f);
        for (int side = -1; side <= 1; side += 2)
        {
            var suffix = side > 0 ? "_l" : "_r";
            var shoulder = RestShoulder(side);
            var elbow = RestElbow(side);
            var wrist = RestWrist(side);
            var upper = Sdf.Capsule(shoulder, elbow, 0.080f).Paint(skin)
                .Union(Sdf.Capsule(shoulder, shoulder.Lerp(elbow, 0.48f), 0.105f).Paint(jersey))
                .Bone("upper_arm" + suffix, shoulder);
            var forearm = Sdf.Capsule(elbow, wrist, 0.075f).Paint(skin)
                .Smin(Sdf.Sphere(0.10f).Move(wrist.X, wrist.Y, wrist.Z).Paint(skin), 0.02f)
                .Bone("forearm" + suffix, elbow);
            model = model.Smin(upper, 0.02f).Smin(forearm, 0.02f);
        }
        return model.Smin(hips, 0.025f);
    }

    static void BuildCharMesh()
    {
        var meshes = charMesh ?? new List<Mesh3d> { new Mesh3d("bb24_char0"), new Mesh3d("bb24_char1") };
        charMesh = meshes;
        for (int team = 0; team < 2; team++)
            meshes[team].Rebuild(Sdf.Mesh(CharModel(teamRgb[team]), 48, 0.025f));
    }

    static Mat4 BoneBetween(Vec3 restStart, Vec3 restEnd, Vec3 start, Vec3 target)
    {
        var a = (restEnd - restStart).Normalize();
        var b = (target - start).Normalize();
        var axis = a.Cross(b);
        float dot = a.Dot(b);
        var rotation = dot < -0.9999f
            ? Quat.FromAxisAngle(a.Cross(Vec3.Right()).Normalize(), (float)Math.PI)
            : new Quat(axis.X, axis.Y, axis.Z, 1 + dot).Normalize();
        return Mat4.Translate(start) * Mat4.FromQuat(rotation) * Mat4.Translate(-restStart);
    }

    static Vec3 BendJoint(Vec3 start, Vec3 target, Vec3 hint, float upper, float lower)
    {
        var delta = target - start;
        float distance = Math.Max(0.001f, delta.Length());
        var direction = delta / distance;
        var bend = hint - start;
        bend = (bend - direction * bend.Dot(direction)).Normalize();
        float along = MathUtil.Clamp((upper * upper - lower * lower + distance * distance) / (2 * distance), -upper, upper);
        float height = (float)Math.Sqrt(Math.Max(0, upper * upper - along * along));
        return start + direction * along + bend * height;
    }

    static BaseballRig MakeRig(BaseballPose p)
    {
        var rig = new BaseballRig();
        var drop = new Vec3(0, p.Drop, p.Shift);
        var torso = Mat4.Translate(drop) * Bones.PivotRot(0, torsoPy, 0,
            Mat4.RotateY(p.Twist) * Mat4.RotateX(p.Lean) * Mat4.RotateZ(p.Tilt));
        rig.Matrices["torso"] = torso;
        rig.Matrices["head"] = torso * Bones.PivotRot(0, headPy, 0,
            Mat4.RotateY(p.HeadYaw) * Mat4.RotateX(p.HeadPitch));
        rig.Matrices["hips"] = Mat4.Translate(drop);
        for (int side = -1; side <= 1; side += 2)
        {
            var suffix = side > 0 ? "_l" : "_r";
            var shoulder = torso.MulPoint(RestShoulder(side));
            var hand = side > 0 ? p.LeftHand : p.RightHand;
            var elbow = p.Bind ? RestElbow(side) : BendJoint(shoulder, hand, torso.MulPoint(new Vec3(-side * 0.60f, 1.05f, -0.15f)),
                RestShoulder(side).Distance(RestElbow(side)), RestElbow(side).Distance(RestWrist(side)));
            rig.Matrices["upper_arm" + suffix] = BoneBetween(RestShoulder(side), RestElbow(side), shoulder, elbow);
            var forearm = BoneBetween(RestElbow(side), RestWrist(side), elbow, hand);
            rig.Matrices["forearm" + suffix] = forearm;
            if (side > 0) rig.LeftHand = forearm.MulPoint(RestWrist(side));
            else rig.RightHand = forearm.MulPoint(RestWrist(side));
            var hip = RestHip(side) + drop;
            var foot = side > 0 ? p.LeftFoot : p.RightFoot;
            var knee = p.Bind ? RestKnee(side) : BendJoint(hip, foot, new Vec3(-side * p.KneeSpread, hip.Y - 0.15f, 0.65f),
                RestHip(side).Distance(RestKnee(side)), RestKnee(side).Distance(RestAnkle(side)));
            rig.Matrices["thigh" + suffix] = BoneBetween(RestHip(side), RestKnee(side), hip, knee);
            var shin = BoneBetween(RestKnee(side), RestAnkle(side), knee, foot);
            rig.Matrices["shin" + suffix] = shin;
            var ankle = shin.MulPoint(RestAnkle(side));
            rig.Matrices["foot" + suffix] = Mat4.Translate(ankle - RestAnkle(side));
        }
        rig.Ball = rig.LeftHand.Lerp(rig.RightHand, p.BallTransfer) + new Vec3(0, 0.04f, 0.09f);
        return rig;
    }

    static List<float> PackRig(BaseballRig rig, MeshData? mesh)
    {
        return Bones.Pack(mesh, (name, px, py, pz) => rig.Matrices.ContainsKey(name) ? rig.Matrices[name] : null);
    }

    static List<float> PackBones(BaseballPose p)
    {
        return PackRig(MakeRig(p), charMesh != null ? charMesh[0].Data : null);
    }

    static BaseballPose ZeroPose() => new BaseballPose();

    static BaseballPose PoseIdle(float t)
    {
        var p = ZeroPose();
        p.Lean = 0.015f * (float)Math.Sin(t * 2.1f);
        p.HeadPitch = -p.Lean;
        return p;
    }

    static BaseballPose PoseReady(float t)
    {
        var p = ZeroPose();
        p.Lean = 0.28f;
        p.Drop = -0.10f;
        p.HeadPitch = -p.Lean;
        p.LeftHand = new Vec3(-0.18f, 1.0f, 0.43f);
        p.RightHand = new Vec3(0.18f, 1.0f, 0.35f);
        p.LeftFoot = new Vec3(-0.26f, 0.12f, 0.03f);
        p.RightFoot = new Vec3(0.26f, 0.12f, -0.03f);
        return p;
    }

    static BaseballPose PoseCrouch(float t)
    {
        var p = PoseReady(t);
        p.Drop = -0.43f;
        p.Lean = 0.28f;
        p.HeadPitch = -p.Lean;
        p.LeftHand = new Vec3(-0.09f, 0.83f, 0.43f);
        p.RightHand = new Vec3(0.18f, 0.55f, 0.12f);
        p.KneeSpread = 0.85f;
        p.LeftFoot = new Vec3(-0.24f, 0.12f, -0.04f);
        p.RightFoot = new Vec3(0.24f, 0.12f, -0.04f);
        return p;
    }

    static Vec3 RunFoot(float phase, float side)
    {
        float cycle = phase / (2 * (float)Math.PI);
        cycle -= (float)Math.Floor(cycle);
        float stride = runSpd * (2 * (float)Math.PI / 11) * 0.28f;
        float z;
        float y = 0.12f;
        if (cycle < 0.28f)
            z = stride * (0.5f - cycle / 0.28f);
        else
        {
            float swing = (cycle - 0.28f) / 0.72f;
            z = stride * (-0.5f + MathUtil.Smoothstep(0, 1, swing));
            y += (float)Math.Sin(swing * (float)Math.PI) * 0.24f;
        }
        return new Vec3(-side * 0.17f, y, z);
    }

    static BaseballPose PoseRun(float ph)
    {
        var p = ZeroPose();
        float swing = (float)Math.Sin(ph);
        p.Drop = -0.28f + 0.02f * (float)Math.Cos(2 * ph);
        p.Lean = 0.24f;
        p.Twist = 0.06f * swing;
        p.HeadPitch = -p.Lean;
        p.HeadYaw = -p.Twist;
        p.LeftFoot = RunFoot(ph, 1);
        p.RightFoot = RunFoot(ph + (float)Math.PI, -1);
        p.LeftHand = new Vec3(-0.31f, 1.00f, -p.LeftFoot.Z * 0.50f);
        p.RightHand = new Vec3(0.31f, 1.00f, -p.RightFoot.Z * 0.50f);
        return p;
    }

    public const float RelPh = 0.60f;

    static BaseballPose PoseWindup(float ph)
    {
        var p = ZeroPose();
        float join = MathUtil.Smoothstep(0, 0.18f, ph);
        float load = MathUtil.Smoothstep(0.20f, 0.45f, ph);
        float strike = MathUtil.Smoothstep(0.50f, RelPh, ph);
        float follow = MathUtil.Smoothstep(RelPh, 0.90f, ph);
        p.Drop = MathUtil.Lerp(0, -0.23f, MathUtil.Smoothstep(0.38f, RelPh, ph));
        p.Twist = MathUtil.Lerp(0.20f * load, -0.30f, strike) - 0.10f * follow;
        p.Lean = MathUtil.Lerp(-0.08f * load, 0.28f, strike) + 0.08f * follow;
        p.HeadPitch = -p.Lean;
        p.HeadYaw = -p.Twist;
        p.LeftHand = RestWrist(1).Lerp(new Vec3(-0.07f, 1.23f, 0.18f), join)
            .Lerp(new Vec3(-0.12f, 1.20f, 0.22f), load)
            .Lerp(new Vec3(-0.18f, 1.02f, 0.14f), strike);
        p.RightHand = RestWrist(-1).Lerp(new Vec3(0.05f, 1.23f, 0.18f), join)
            .Lerp(new Vec3(0.44f, 1.55f, -0.17f), load)
            .Lerp(new Vec3(0.14f, 1.57f, 0.60f), strike)
            .Lerp(new Vec3(-0.20f, 0.97f, 0.40f), follow);
        float lift = MathUtil.Smoothstep(0.05f, 0.35f, ph);
        float step = MathUtil.Smoothstep(0.38f, 0.56f, ph);
        p.LeftFoot = RestAnkle(1).Lerp(new Vec3(-0.16f, 0.65f, 0.15f), lift)
            .Lerp(new Vec3(-0.20f, 0.12f, 0.47f), step);
        p.RightFoot = RestAnkle(-1).Lerp(new Vec3(0.20f, 0.12f, -0.12f), step);
        p.BallTransfer = MathUtil.Smoothstep(0.16f, 0.24f, ph);
        return p;
    }

    public const float SwingHitPh = 0.52f;
    static float batContactX = 0, batContactY = 1, batContactZ = 0.35f;

    static Mat4 BatLocalMatrix(float ph)
    {
        float strike = MathUtil.Smoothstep(0.32f, SwingHitPh, ph);
        float follow = MathUtil.Smoothstep(SwingHitPh, 0.92f, ph);
        var contactGrip = new Vec3(0, MathUtil.Clamp(batContactY + 0.15f, 0.55f, 1.30f), MathUtil.Clamp(batContactX + 0.24f, 0.05f, 0.50f));
        var grip = new Vec3(0.16f, 1.35f, 0.20f).Lerp(contactGrip, strike)
            .Lerp(new Vec3(-0.16f, 1.42f, 0), follow);
        var contact = new Vec3(-batContactZ, batContactY, batContactX + 0.85f) - contactGrip;
        float hitYaw = (float)Math.Atan2(contact.X, contact.Z);
        float hitTilt = -(float)Math.Atan2(contact.Y, (float)Math.Sqrt(contact.X * contact.X + contact.Z * contact.Z));
        float yaw = MathUtil.Lerp(2.35f, hitYaw, strike) - 1.3f * follow;
        float tilt = MathUtil.Lerp(-1.05f, hitTilt, strike);
        tilt = MathUtil.Lerp(tilt, -0.90f, follow);
        return Mat4.Translate(grip) * Mat4.RotateY(yaw) * Mat4.RotateX(tilt);
    }

    static BaseballPose PoseSwing(float ph)
    {
        var p = ZeroPose();
        float strike = MathUtil.Smoothstep(0.32f, SwingHitPh, ph);
        float follow = MathUtil.Smoothstep(SwingHitPh, 0.92f, ph);
        p.Drop = MathUtil.Lerp(-0.10f, MathUtil.Clamp(-0.18f + (batContactY - 1) * 0.70f, -0.62f, -0.18f), strike);
        p.Drop = MathUtil.Lerp(p.Drop, -0.10f, follow);
        p.Shift = Math.Max(0, batContactX) * 0.75f * strike * (1 - follow);
        p.Lean = 0.08f + Math.Max(0, 1 - batContactY) * 0.15f * strike * (1 - follow);
        p.Twist = MathUtil.Lerp(0.12f, -1.35f, strike) - 0.45f * follow;
        p.HeadYaw = -(float)Math.PI / 2 - p.Twist;
        p.HeadPitch = -p.Lean;
        var bat = BatLocalMatrix(ph);
        p.LeftHand = bat.MulPoint(new Vec3(0, 0, 0));
        p.RightHand = bat.MulPoint(new Vec3(0, 0, 0.11f));
        p.LeftFoot = new Vec3(-0.30f - 0.10f * strike, 0.12f, 0.02f);
        p.RightFoot = new Vec3(0.30f, 0.12f, 0.02f);
        return p;
    }

    static BaseballPose PoseReach(float t)
    {
        var p = ZeroPose();
        p.HeadPitch = -0.60f;
        p.LeftHand = new Vec3(-0.18f, 1.90f, 0.27f);
        p.RightHand = new Vec3(0.25f, 1.57f, 0.30f);
        return p;
    }

    static BaseballPose PoseThrow(float ph)
    {
        var p = PoseReady(0);
        float load = MathUtil.Smoothstep(0.20f, 0.40f, ph);
        float release = MathUtil.Smoothstep(0.40f, 0.24f / 0.45f, ph);
        float follow = MathUtil.Smoothstep(0.24f / 0.45f, 0.90f, ph);
        p.Drop = -0.12f;
        p.Twist = MathUtil.Lerp(0.25f * load, -0.25f, release);
        p.Lean = 0.10f + 0.15f * release;
        p.HeadPitch = -p.Lean;
        p.HeadYaw = -p.Twist;
        p.LeftHand = new Vec3(-0.08f, 1.03f, 0.30f).Lerp(new Vec3(-0.18f, 1.0f, 0.08f), load);
        p.RightHand = new Vec3(0.03f, 1.03f, 0.30f)
            .Lerp(new Vec3(0.42f, 1.45f, -0.15f), load)
            .Lerp(new Vec3(0.10f, 1.36f, 0.63f), release)
            .Lerp(new Vec3(-0.20f, 0.95f, 0.30f), follow);
        p.BallTransfer = MathUtil.Smoothstep(0.10f, 0.28f, ph);
        return p;
    }

    static BaseballPose BlendPose(BaseballPose a, BaseballPose b, float k)
    {
        return new BaseballPose
        {
            Twist = MathUtil.Lerp(a.Twist, b.Twist, k),
            Lean = MathUtil.Lerp(a.Lean, b.Lean, k),
            Tilt = MathUtil.Lerp(a.Tilt, b.Tilt, k),
            Drop = MathUtil.Lerp(a.Drop, b.Drop, k),
            Shift = MathUtil.Lerp(a.Shift, b.Shift, k),
            KneeSpread = MathUtil.Lerp(a.KneeSpread, b.KneeSpread, k),
            HeadPitch = MathUtil.Lerp(a.HeadPitch, b.HeadPitch, k),
            HeadYaw = MathUtil.Lerp(a.HeadYaw, b.HeadYaw, k),
            BallTransfer = MathUtil.Lerp(a.BallTransfer, b.BallTransfer, k),
            LeftHand = a.LeftHand.Lerp(b.LeftHand, k),
            RightHand = a.RightHand.Lerp(b.RightHand, k),
            LeftFoot = a.LeftFoot.Lerp(b.LeftFoot, k),
            RightFoot = a.RightFoot.Lerp(b.RightFoot, k)
        };
    }

    static BaseballPose PoseFor(int anim, float t, float runPhase)
    {
        if (anim == AnReady) return PoseReady(t);
        if (anim == AnRun) return PoseRun(runPhase);
        if (anim == AnWindup) return PoseWindup(t);
        if (anim == AnSwing) return PoseSwing(t);
        if (anim == AnReach) return PoseReach(t);
        if (anim == AnCrouch) return PoseCrouch(t);
        if (anim == AnThrow) return PoseThrow(t);
        return PoseIdle(t);
    }

    // --- 静的メッシュ (Shapes) ----------------------------------------------
    static Mesh3d? fieldMesh = null;
    static Mesh3d? ballMesh = null;
    static Mesh3d? batMesh = null;
    static Mesh3d? gloveMesh = null;

    static void Fan(List<float> dst, float cx, float cy, float cz,
        float r, float a0, float a1, int segs, List<float> col)
    {
        for (int i = 0; i < segs; i++)
        {
            var t0 = a0 + (a1 - a0) * i / segs;
            var t1 = a0 + (a1 - a0) * (i + 1) / segs;
            Shapes.Tri(dst, new List<float> { cx, cy, cz },
                new List<float>
                    { cx + (float)Math.Sin(t0) * r, cy, cz + (float)Math.Cos(t0) * r },
                new List<float>
                    { cx + (float)Math.Sin(t1) * r, cy, cz + (float)Math.Cos(t1) * r },
                new List<float> { 0, 1, 0 }, col);
        }
    }

    static void BuildField()
    {
        var fm = fieldMesh ?? new Mesh3d("bb24_field");
        fieldMesh = fm;
        var bm = ballMesh ?? new Mesh3d("bb24_ball");
        ballMesh = bm;
        var btm = batMesh ?? new Mesh3d("bb24_bat");
        batMesh = btm;

        var v = new List<float>();
        var grass = new List<float> { 0.19f, 0.37f, 0.29f, 1.0f };
        var grassIn = new List<float> { 0.31f, 0.55f, 0.34f, 1.0f };
        var dirt = new List<float> { 0.73f, 0.49f, 0.32f, 1.0f };
        var lineW = new List<float> { 0.95f, 0.95f, 0.92f, 1.0f };
        var wall = new List<float> { 0.12f, 0.26f, 0.29f, 1.0f };
        var wallTop = new List<float> { 0.88f, 0.82f, 0.35f, 1.0f };
        var up = new List<float> { 0, 1, 0 };

        // 地面 (ファウルグラウンド込みの外周)
        Shapes.Quad(v, new List<float> { -95, 0, -20 },
            new List<float> { -95, 0, 95 }, new List<float> { 95, 0, 95 },
            new List<float> { 95, 0, -20 }, up, grass);
        // フェアグラウンドの扇形 (少し明るい緑)
        Fan(v, 0, 0.012f, 0, fenceR, -(float)Math.PI / 4, (float)Math.PI / 4, 24, grassIn);
        for (int band = 0; band < 12; band++)
        {
            var z0 = band * 6.0f;
            var z1 = z0 + 3.0f;
            var edge0 = Math.Min(z0, (float)Math.Sqrt(fenceR * fenceR - z0 * z0));
            var edge1 = Math.Min(z1, (float)Math.Sqrt(fenceR * fenceR - z1 * z1));
            Shapes.Quad(v, new List<float> { -edge0, 0.016f, z0 },
                new List<float> { -edge1, 0.016f, z1 }, new List<float> { edge1, 0.016f, z1 },
                new List<float> { edge0, 0.016f, z0 }, up,
                new List<float> { 0.27f, 0.48f, 0.30f, 1 });
        }
        // 内野ダート (ひし形)
        Shapes.Quad(v, new List<float> { 0, 0.024f, -2.2f },
            new List<float> { 24.5f, 0.024f, baseD },
            new List<float> { 0, 0.024f, 43.0f },
            new List<float> { -24.5f, 0.024f, baseD }, up, dirt);
        // 内野の芝 (ダートの内側)
        Shapes.Quad(v, new List<float> { 0, 0.036f, 4.2f },
            new List<float> { 15.5f, 0.036f, baseD },
            new List<float> { 0, 0.036f, 34.6f },
            new List<float> { -15.5f, 0.036f, baseD }, up, grassIn);
        // マウンド (つぶれたドーム + ダート円)
        Fan(v, 0, 0.048f, moundZ, 2.9f, -(float)Math.PI, (float)Math.PI, 16, dirt);
        Shapes.Sphere(v, 0, -2.35f, moundZ, 2.6f, dirt, 8, 16);
        // 本塁と各塁
        Shapes.Box(v, 0, 0.03f, 0, 0.55f, 0.05f, 0.55f, lineW);
        Shapes.Box(v, baseD, 0.07f, baseD, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.Box(v, 0, 0.07f, baseD * 2, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.Box(v, -baseD, 0.07f, baseD, 0.55f, 0.13f, 0.55f, lineW);
        // プレート (マウンド上)
        Shapes.Box(v, 0, 0.30f, moundZ, 0.61f, 0.05f, 0.15f, lineW);
        // ファウルライン
        var d = 0.70710678f;
        foreach (var s in new List<float> { -1.0f, 1.0f })
        {
            var nx = -s * d; // ライン直交方向
            var nz = d;
            var half = 0.09f;
            var x0 = s * 1.2f * d;
            var z0 = 1.2f * d;
            var x1 = s * (fenceR - 0.6f) * d;
            var z1 = (fenceR - 0.6f) * d;
            Shapes.Quad(v,
                new List<float> { x0 - nx * half, 0.045f, z0 - nz * half },
                new List<float> { x1 - nx * half, 0.045f, z1 - nz * half },
                new List<float> { x1 + nx * half, 0.045f, z1 + nz * half },
                new List<float> { x0 + nx * half, 0.045f, z0 + nz * half },
                up, lineW);
        }
        // 外野フェンス (内向きの壁 + 黄色いトップ)
        int segs = 26;
        for (int i = 0; i < segs; i++)
        {
            var a0 = -(float)Math.PI / 4 + (float)Math.PI / 2 * i / segs;
            var a1 = -(float)Math.PI / 4 + (float)Math.PI / 2 * (i + 1) / segs;
            var x0 = (float)Math.Sin(a0) * fenceR;
            var z0 = (float)Math.Cos(a0) * fenceR;
            var x1 = (float)Math.Sin(a1) * fenceR;
            var z1 = (float)Math.Cos(a1) * fenceR;
            var am = (a0 + a1) * 0.5f;
            var n = new List<float> { -(float)Math.Sin(am), 0, -(float)Math.Cos(am) };
            Shapes.Quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, fenceH, z0 },
                new List<float> { x1, fenceH, z1 },
                new List<float> { x1, 0, z1 }, n, wall);
            Shapes.Quad(v, new List<float> { x0, fenceH, z0 },
                new List<float> { x0, fenceH + 0.18f, z0 },
                new List<float> { x1, fenceH + 0.18f, z1 },
                new List<float> { x1, fenceH, z1 }, n, wallTop);
        }
        // バックストップ (本塁後方の低い壁)
        int bsegs = 10;
        var bsCol = new List<float> { 0.48f, 0.51f, 0.55f, 1.0f };
        for (int i = 0; i < bsegs; i++)
        {
            var a0 = (float)Math.PI * 0.75f + (float)Math.PI * 0.5f * i / bsegs;
            var a1 = (float)Math.PI * 0.75f + (float)Math.PI * 0.5f * (i + 1) / bsegs;
            var r = 11.5f;
            var x0 = (float)Math.Sin(a0) * r;
            var z0 = (float)Math.Cos(a0) * r;
            var x1 = (float)Math.Sin(a1) * r;
            var z1 = (float)Math.Cos(a1) * r;
            var am = (a0 + a1) * 0.5f;
            var n = new List<float> { -(float)Math.Sin(am), 0, -(float)Math.Cos(am) };
            Shapes.Quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, 1.6f, z0 },
                new List<float> { x1, 1.6f, z1 },
                new List<float> { x1, 0, z1 }, n, bsCol);
        }
        var seat = new List<float> { 0.16f, 0.30f, 0.36f, 1 };
        var concrete = new List<float> { 0.60f, 0.64f, 0.60f, 1 };
        for (int side = -1; side <= 1; side += 2)
        {
            for (int row = 0; row < 7; row++)
            {
                float z = -14 - row * 1.5f;
                float y = 1.6f + row * 0.8f;
                Shapes.Box(v, side * 11, y - 0.5f, z, 19, 0.6f, 1.5f, concrete);
                for (int j = 0; j < 22; j++)
                {
                    float x = side * 11 - 8.8f + j * 0.84f;
                    Shapes.Box(v, x, y, z, 0.64f, 0.7f, 0.25f, seat);
                    var crowd = (j * 7 + row * 3) % 5;
                    var shirt = crowd == 0 ? new List<float> { 0.84f, 0.40f, 0.28f, 1 }
                        : crowd == 1 ? lineW : crowd == 2 ? wallTop : seat;
                    Shapes.Box(v, x, y + 0.16f, z + 0.36f, 0.43f, 0.58f, 0.30f, shirt);
                    Shapes.Sphere(v, x, y + 0.64f, z + 0.36f, 0.19f,
                        new List<float> { 0.80f, 0.62f, 0.46f, 1 }, 4, 6);
                }
            }
            Shapes.Box(v, side * 11, 9.5f, -23, 20, 0.22f, 8, wall);
            for (int j = 0; j < 3; j++)
                Shapes.Box(v, side * 11 - 9 + j * 9, 4.75f, -25, 0.22f, 9.5f, 0.22f, seat);
            Shapes.Box(v, side * 34, 10, -8, 0.36f, 20, 0.36f, concrete);
            Shapes.Box(v, side * 34, 20, -8, 6, 2, 0.6f, seat);
            for (int j = 0; j < 6; j++)
                Shapes.Box(v, side * 34 - 2.5f + j, 20, -7.66f, 0.65f, 1.4f, 0.05f, lineW);
        }
        for (int side = -1; side <= 1; side += 2)
        {
            Shapes.Box(v, side * 1.20f, 0.055f, 0, 0.045f, 0.02f, 2.2f, lineW);
            Shapes.Box(v, side * 2.45f, 0.055f, 0, 0.045f, 0.02f, 2.2f, lineW);
            Shapes.Box(v, side * 1.82f, 0.055f, -1.1f, 1.3f, 0.02f, 0.045f, lineW);
            Shapes.Box(v, side * 1.82f, 0.055f, 1.1f, 1.3f, 0.02f, 0.045f, lineW);
        }
        fm.Rebuild(Shapes3d.FromInterleaved(v));

        var ballVerts = new List<float>();
        Shapes.Sphere(ballVerts, 0, 0, 0, ballR,
            new List<float> { 0.96f, 0.96f, 0.94f, 1.0f }, 8, 12);
        bm.Rebuild(Shapes3d.FromInterleaved(ballVerts));

        btm.Rebuild(Sdf.Mesh(Sdf.Capsule(new Vec3(0, 0, 0.08f), new Vec3(0, 0, 0.45f), 0.035f)
            .Paint(0x283648)
            .Smin(Sdf.Capsule(new Vec3(0, 0, 0.40f), new Vec3(0, 0, 0.94f), 0.075f)
                .Paint(0xDDA75F, 0, 0.4f), 0.06f), 40));
        var gm = gloveMesh ?? new Mesh3d("bb24_glove");
        gloveMesh = gm;
        gm.Rebuild(Sdf.Mesh(Sdf.Sphere(0.17f)
            .Subtract(Sdf.Sphere(0.14f).Move(0, 0, 0.11f))
            .Smin(Sdf.Capsule(new Vec3(-0.12f, -0.08f, 0), new Vec3(-0.15f, 0.10f, 0.04f), 0.06f), 0.04f)
            .Paint(0xAD683B, 0, 0.75f), 32));
    }

    // --- ボール ---------------------------------------------------------------
    static float bx = 0.0f;
    static float by = 0.0f;
    static float bz = 0.0f;
    static float bvx = 0.0f;
    static float bvy = 0.0f;
    static float bvz = 0.0f;
    static bool ballVisible = false;
    static int ballBounces = 0;
    static bool ballRolling = false;
    static bool isHomeRun = false;

    // 打球の 1 step (共通 integrator)。返り値: バウンドしたか
    static bool StepBall(float dt, bool drag)
    {
        if (drag)
        {
            var sp = (float)Math.Sqrt(bvx * bvx + bvy * bvy + bvz * bvz);
            var f = 1.0f / (1.0f + dragCoef * sp * dt);
            bvx *= f;
            bvy *= f;
            bvz *= f;
        }
        bvy -= grav * dt;
        bx += bvx * dt;
        by += bvy * dt;
        bz += bvz * dt;
        var bounced = false;
        // 地面
        if (by < ballR && bvy < 0)
        {
            by = ballR;
            if (Math.Abs(bvy) < 1.0f)
            {
                ballRolling = true;
                bvy = 0;
            }
            else
            {
                bvy = -bvy * 0.42f;
                bounced = true;
                ballBounces++;
            }
            bvx *= 0.72f;
            bvz *= 0.72f;
        }
        if (ballRolling)
        {
            by = ballR;
            bvy = 0;
            var sp = (float)Math.Sqrt(bvx * bvx + bvz * bvz);
            if (sp > 0)
            {
                var dec = Math.Max(0.0f, sp - 2.6f * dt);
                bvx *= dec / sp;
                bvz *= dec / sp;
            }
        }
        // フェンス (フェア扇形内の円筒壁)。越えたら本塁打
        var hr = (float)Math.Sqrt(bx * bx + bz * bz);
        if (!isHomeRun && bz > 0 && Math.Abs(bx) < bz + 2 && hr > fenceR - ballR)
        {
            if (by > fenceH)
            {
                if (!isHomeRun && ballBounces == 0)
                    isHomeRun = true;
            }
            else
            {
                // 半径方向の反射
                var nx = bx / hr;
                var nz = bz / hr;
                var vr = bvx * nx + bvz * nz;
                if (vr > 0)
                {
                    bvx -= 1.4f * vr * nx;
                    bvz -= 1.4f * vr * nz;
                    bx = nx * (fenceR - ballR);
                    bz = nz * (fenceR - ballR);
                    ballBounces++;
                }
            }
        }
        return bounced;
    }

    // 着地予測 (状態を退避してシミュレート)
    static Landing PredictLanding()
    {
        var sx = bx;
        var sy = by;
        var sz = bz;
        var svx = bvx;
        var svy = bvy;
        var svz = bvz;
        var sb = ballBounces;
        var sr = ballRolling;
        var shr = isHomeRun;
        var t = 0.0f;
        var peak = by;
        while (t < 12.0f)
        {
            StepBall(tickDt, true);
            t += tickDt;
            if (by > peak)
                peak = by;
            if (ballBounces > sb || ballRolling)
                break;
        }
        var r = new Landing { X = bx, Z = bz, T = t, Peak = peak };
        bx = sx;
        by = sy;
        bz = sz;
        bvx = svx;
        bvy = svy;
        bvz = svz;
        ballBounces = sb;
        ballRolling = sr;
        isHomeRun = shr;
        return r;
    }

    // --- チームと選手 -----------------------------------------------------------
    static string[] teamName = new string[] { "RED", "BLUE" };

    // 9人 (0=P 1=C 2=1B 3=2B 4=3B 5=SS 6=LF 7=CF 8=RF)
    static List<Fielder>? fielders = null;
    static Fielder? batter = null;
    static List<Runner>? runners = null;

    static List<List<float>> FielderHomes()
    {
        return new List<List<float>>
        {
            new List<float> { 0.0f, moundZ }, // P
            new List<float> { 0.0f, -2.4f }, // C
            new List<float> { 21.0f, 18.5f }, // 1B
            new List<float> { 11.0f, 31.0f }, // 2B
            new List<float> { -21.0f, 18.5f }, // 3B
            new List<float> { -11.0f, 31.0f }, // SS
            new List<float> { -27.0f, 56.0f }, // LF
            new List<float> { 0.0f, 63.0f }, // CF
            new List<float> { 27.0f, 56.0f }, // RF
        };
    }

    static List<float> BasePos(int i)
    {
        if (i == 1) return new List<float> { baseD, baseD };
        if (i == 2) return new List<float> { 0.0f, baseD * 2 };
        if (i == 3) return new List<float> { -baseD, baseD };
        return new List<float> { 0.0f, 0.0f }; // 0 と 4 は本塁
    }

    static void ResetActors()
    {
        var fs = new List<Fielder>();
        foreach (var h in FielderHomes())
            fs.Add(new Fielder(h[0], h[1]));
        fielders = fs;
        batter = new Fielder(-0.85f, 0.0f);
        runners = new List<Runner>();
        batterRunner = null;
        retiredRunner = null;
        batterAtPlate = true;
        ballHeldBy = -1;
        ballVisible = false;
    }

    // --- 試合状態 ---------------------------------------------------------------
    static int state = stIntro;
    static float stateT = 0.0f;
    static int inning = 1;
    static int half = 0; // 0=表 (RED 攻撃) 1=裏
    static List<int> score = new List<int> { 0, 0 };
    static int balls = 0;
    static int strikes = 0;
    static int outs = 0;

    static int BattingTeam()
    {
        return half == 0 ? 0 : 1;
    }

    static int FieldingTeam()
    {
        return half == 0 ? 1 : 0;
    }

    // 投球ごとの判定材料
    static float pitchTX = 0.0f; // 到達点 (x, y)
    static float pitchTY = 0.0f;
    static bool pitchInZone = false;
    static bool willSwing = false;
    static int swingOutcome = 0; // 0=空振り 1=ファウル 2=インプレー
    static float exitSpeed = 0.0f;
    static float exitLaunch = 0.0f;
    static float exitSpray = 0.0f;
    static bool swingStarted = false;

    // ST_LIVE の進行
    static int playPhase = plFly;
    static int chaser = -1;
    static int ballHeldBy = -1; // 野手 index (-1 = フリー)
    static float liveT = 0.0f;
    static float throwT = 0.0f;
    static float throwDur = 0.0f;
    static float throwFromX = 0.0f;
    static float throwFromY = 0.0f;
    static float throwFromZ = 0.0f;
    static Runner? batterRunner = null;
    static Runner? retiredRunner = null;
    static int firstBaseCover = 2;
    static bool batterAtPlate = true;
    static Landing? landing = null;

    // 演出
    static float hitstopT = 0.0f;
    static string eventText = "";
    static float eventT = 99.0f;
    static Color? eventCol = null;
    static float tAccum = 0.0f;

    static void ShowEvent(string s, Color? c)
    {
        eventText = s;
        eventT = 0.0f;
        eventCol = c ?? Color.Rgb(1.0f, 0.98f, 0.9f);
    }

    static void SetState(int s)
    {
        state = s;
        stateT = 0.0f;
    }

    // --- 投球開始 -----------------------------------------------------------------
    static void StartPitch()
    {
        var fs = fielders;
        if (fs == null)
            return;
        foreach (var f in fs) f.CatchPose = null;
        // 目標: ゾーン内/外を先に決めてから座標を出す
        pitchInZone = Rnd() < 0.62f;
        if (pitchInZone)
        {
            pitchTX = Rrange(-0.20f, 0.20f);
            pitchTY = Rrange(0.60f, 1.10f);
        }
        else
        {
            // ゾーンの少し外
            if (Rnd() < 0.5f)
            {
                pitchTX = (Rnd() < 0.5f ? -1.0f : 1.0f) * Rrange(0.28f, 0.45f);
                pitchTY = Rrange(0.45f, 1.25f);
            }
            else
            {
                pitchTX = Rrange(-0.35f, 0.35f);
                pitchTY = Rnd() < 0.5f ? Rrange(0.15f, 0.42f) : Rrange(1.28f, 1.55f);
            }
        }
        willSwing = Rnd() < (pitchInZone ? 0.80f : 0.26f);
        if (willSwing)
        {
            var r = Rnd();
            if (r < 0.24f)
                swingOutcome = 0;
            else if (r < 0.58f)
                swingOutcome = 1;
            else
            {
                swingOutcome = 2;
                exitSpeed = 23.0f + 23.0f * (float)Math.Pow(Rnd(), 0.7f);
                exitLaunch = Rrange(-6.0f, 42.0f);
                exitSpray = Rrange(-38.0f, 38.0f);
            }
        }
        swingStarted = false;
        showAllBases = runners != null && runners.Count > 0;
        ballHeldBy = 0;
        ballVisible = true;
        SetState(stWindup);
        fs[0].Anim = AnWindup;
        fs[0].AnimT = 0;
    }

    // リリース: ボールに初速を与える (重力補償で目標へ届ける)
    static void ReleaseBall()
    {
        var fs = fielders;
        if (fs == null) return;
        var origin = FielderBall(0);
        bx = origin.X;
        by = origin.Y;
        bz = origin.Z;
        batContactX = pitchTX;
        batContactY = pitchTY;
        batContactZ = 0.42f;
        var speed = Rrange(31.0f, 40.0f);
        var dz = 0.42f - bz;
        var t = Math.Abs(dz) / speed;
        bvx = (pitchTX - bx) / t;
        bvy = (pitchTY - by) / t + 0.5f * grav * t;
        bvz = dz / t;
        ballVisible = true;
        ballHeldBy = -1;
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        SetState(stPitch);
    }

    // --- 打撃結果の解決 -------------------------------------------------------------
    static void ResolveContact()
    {
        var fs = fielders;
        var b = batter;
        var rns = runners;
        if (fs == null || b == null || rns == null)
            return;
        if (!willSwing || swingOutcome == 0)
        {
            // 見送り or 空振り → カウント
            if (willSwing)
            {
                strikes++;
                ShowEvent("SWING & MISS", null);
            }
            else if (pitchInZone)
            {
                strikes++;
                ShowEvent("STRIKE", null);
            }
            else
            {
                balls++;
                ShowEvent("BALL", null);
            }
            fs[1].Anim = AnReach;
            fs[1].AnimT = 0;
            fs[1].CatchPose = CatchPose(1, new Vec3(bx, by, bz));
            ballHeldBy = 1;
            AfterCall();
            return;
        }
        // 接触を短く止め、振り抜きへつなぐ。
        hitstopT = 0.035f;
        var launch = exitLaunch;
        var spray = exitSpray;
        var speed = exitSpeed;
        if (swingOutcome == 1)
        {
            // ファウル: 打球はラインの外か後方へ
            speed = Rrange(16.0f, 34.0f);
            launch = Rrange(15.0f, 70.0f);
            spray = (Rnd() < 0.5f ? -1.0f : 1.0f) * Rrange(50.0f, 130.0f);
        }
        var la = MathUtil.Radians(launch);
        var sa = MathUtil.Radians(spray);
        b.AnimT = SwingHitPh;
        bvx = speed * (float)Math.Cos(la) * (float)Math.Sin(sa);
        bvy = speed * (float)Math.Sin(la);
        bvz = speed * (float)Math.Cos(la) * (float)Math.Cos(sa);
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        var land = PredictLanding();
        landing = land;
        liveT = 0.0f;
        ballHeldBy = -1;
        if (swingOutcome == 1)
        {
            playPhase = plFoul;
            SetState(stLive);
            return;
        }
        // 打者走者スタート
        var br = new Runner(b.X, b.Z, 0, 1);
        batterRunner = br;
        batterAtPlate = false;
        rns.Add(br);
        b.Anim = AnSwing; // 走り出しはスイングの続きから
        // 最寄りの野手が追う
        chaser = NearestFielder(land.X, land.Z);
        firstBaseCover = chaser == 2 ? 0 : 2;
        playPhase = plFly;
        SetState(stLive);
    }

    static int NearestFielder(float x, float z)
    {
        var fs = fielders;
        if (fs == null)
            return -1;
        var best = -1;
        var bd = 1e9f;
        // 投手と捕手は追走から除外 (定位置が近すぎて何でも取ってしまう)
        for (int i = 2; i < 9; i++)
        {
            var f = fs[i];
            var d = (f.X - x) * (f.X - x) + (f.Z - z) * (f.Z - z);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }
        return best;
    }

    // 打席の結果が確定 (カウント系)。四球/三振/次打者を処理
    static void AfterCall()
    {
        SetState(stCall);
        var b = batter;
        var rns = runners;
        if (b == null || rns == null)
            return;
        if (strikes >= 3)
        {
            ShowEvent("STRIKE OUT!", Color.Rgb(1.0f, 0.5f, 0.3f));
            outs++;
            newBatterPending = true;
        }
        else if (balls >= 4)
        {
            ShowEvent("WALK", Color.Rgb(0.5f, 0.9f, 1.0f));
            // 押し出し: 1塁から連続で埋まっている走者だけ 1 つ進む
            var occ = new Dictionary<int, Runner>();
            foreach (var r in rns)
                occ[r.To] = r;
            int free = 1;
            while (occ.ContainsKey(free))
                free++;
            for (int bs = 1; bs < free; bs++)
                occ[bs].To = bs + 1;
            rns.Add(new Runner(b.X, b.Z, 0, 1));
            batterAtPlate = false;
            newBatterPending = true;
        }
    }

    static bool newBatterPending = false;

    // --- 野手 AI (ST_LIVE) ------------------------------------------------------------
    static bool MoveTowards(Fielder f, float tx, float tz, float dt,
        float spd)
    {
        var dx = tx - f.X;
        var dz = tz - f.Z;
        var d = (float)Math.Sqrt(dx * dx + dz * dz);
        if (d < 0.15f)
        {
            if (f.Anim == AnRun)
                f.Anim = AnReady;
            return true;
        }
        var mv = Math.Min(d, spd * dt);
        f.X += dx / d * mv;
        f.Z += dz / d * mv;
        f.Yaw = (float)Math.Atan2(dx, dz);
        f.Anim = AnRun;
        f.RunPhase += mv / runSpd * 11.0f;
        return false;
    }

    static void UpdateLive(float dt)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        liveT += dt;
        if (playPhase == plFly || playPhase == plFoul || isHomeRun && ballVisible)
            StepBall(dt, true);
        if (isHomeRun && ballBounces > 0)
            ballVisible = false;

        if (playPhase == plFoul)
        {
            if (liveT > 1.25f)
            {
                if (strikes < 2)
                    strikes++;
                ShowEvent("FOUL", null);
                ballVisible = false;
                var brf = batterRunner;
                if (brf != null)
                    rns.Remove(brf);
                batterRunner = null;
                AfterCall();
            }
            return;
        }

        if (isHomeRun && playPhase == plFly)
        {
            ShowEvent("HOME RUN!", Color.Rgb(1.0f, 0.85f, 0.25f));
            foreach (var r in rns)
                r.To = 4;
            playPhase = plSettle;
        }

        // 走者更新
        UpdateRunners(dt, 1.0f);

        // 野手: 追走者は打球へ、一塁手はベースカバー、他は定位置へ
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            if (state != stLive || playPhase == plSettle)
                break;
            if (i == chaser && ballHeldBy < 0 && playPhase == plFly)
            {
                // 落下点 (フライ) or 転がるボールの少し先 (ゴロ)
                var land = landing;
                var flying = ballBounces == 0 && !ballRolling;
                var tx = flying && land != null ? land.X : bx + bvx * 0.35f;
                var tz = flying && land != null ? land.Z : bz + bvz * 0.35f;
                var radius = (float)Math.Sqrt(tx * tx + tz * tz);
                if (radius > fenceR - 1.2f)
                {
                    tx *= (fenceR - 1.2f) / radius;
                    tz *= (fenceR - 1.2f) / radius;
                }
                float approach = (float)Math.Atan2(bx - f.X, bz - f.Z);
                bool reaching = (f.X - bx) * (f.X - bx) + (f.Z - bz) * (f.Z - bz) < 6.25f && by < 2.0f;
                if (reaching)
                {
                    var offset = Mat4.RotateY(approach).MulDir(new Vec3(-0.15f, 0, 0.45f));
                    tx = bx - offset.X;
                    tz = bz - offset.Z;
                }
                var arrived = MoveTowards(f, tx, tz, dt, runSpd);
                if (reaching) f.Yaw = approach;
                var dx = f.X - bx;
                var dz = f.Z - bz;
                var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                bool canCatch = false;
                if (reaching && dist < catchR)
                {
                    var rig = MakeRig(CatchPose(i, new Vec3(bx, by, bz)));
                    var glove = (Mat4.Translate(new Vec3(f.X, 0, f.Z)) * Mat4.RotateY(FielderYaw(i))).MulPoint(rig.Ball);
                    canCatch = glove.Distance(new Vec3(bx, by, bz)) < 0.01f;
                }
                if (flying && bvy < 0 && canCatch)
                {
                    // ノーバウンド捕球 → アウト
                    FielderCaught(i, true);
                }
                else if ((ballBounces > 0 || ballRolling)
                    && canCatch && by < 1.2f)
                {
                    FielderCaught(i, false);
                }
                else if (arrived && (ballBounces > 0 || ballRolling))
                {
                    f.Anim = AnReady;
                }
            }
            else if (i == firstBaseCover && batterRunner != null)
            {
                // 一塁手はベースへ (自分が追走者でなければ)
                MoveTowards(f, baseD - 0.25f, baseD - 0.25f, dt, runSpd);
            }
            else if (i != chaser)
            {
                MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8f);
            }
        }

        // 一塁送球の到達判定
        if (playPhase == plThrow1b)
        {
            throwT += dt;
            if (ballHeldBy == chaser)
            {
                var origin = FielderBall(chaser);
                throwFromX = origin.X;
                throwFromY = origin.Y;
                throwFromZ = origin.Z;
                if (throwT >= 0.24f) ballHeldBy = -1;
            }
            var receiver = fs[firstBaseCover];
            receiver.Yaw = (float)Math.Atan2(throwFromX - receiver.X, throwFromZ - receiver.Z);
            if (receiver.Anim != AnRun) receiver.Anim = AnReady;
            var destination = FielderBall(firstBaseCover);
            var k = MathUtil.Clamp((throwT - 0.24f) / throwDur, 0, 1);
            bx = MathUtil.Lerp(throwFromX, destination.X, k);
            bz = MathUtil.Lerp(throwFromZ, destination.Z, k);
            by = MathUtil.Lerp(throwFromY, destination.Y, k) + (float)Math.Sin(k * (float)Math.PI) * 1.4f;
            if (k >= 1.0f)
            {
                ballHeldBy = firstBaseCover;
                playPhase = plTouch1b;
            }
        }
        if (playPhase == plTouch1b)
        {
            var cover = fs[firstBaseCover];
            float dx = cover.X - baseD;
            float dz = cover.Z - baseD;
            if (dx * dx + dz * dz < 0.36f)
            {
                // 封殺 or セーフ: 走者の進塁具合と競争
                var br = batterRunner;
                if (br != null && br.AtBase < 1)
                {
                    outs++;
                    ShowEvent("OUT!", Color.Rgb(1.0f, 0.5f, 0.3f));
                    retiredRunner = br;
                    rns.Remove(br);
                    // 他の走者は 1 つ進む
                    foreach (var r in rns)
                        if (r.To < 3)
                            r.To++;
                }
                else
                {
                    ShowEvent("SAFE!", Color.Rgb(0.5f, 1.0f, 0.6f));
                }
                batterRunner = null;
                cover.Anim = AnReady;
                cover.AnimT = 0;
                playPhase = plSettle;
                stateT = 0;
            }
        }

        // 決着: 走者が全員目標に着いたら打席交代
        if (playPhase == plSettle)
        {
            var settled = true;
            foreach (var r in rns)
                if (r.AtBase < r.To)
                    settled = false;
            if (settled && stateT > 0.65f)
            {
                ballVisible = false;
                newBatterPending = true;
                SetState(stCall);
            }
        }
        // 保険: 異常に長引いたら打ち切り
        if (liveT > 14.0f && playPhase != plSettle)
        {
            ballVisible = false;
            newBatterPending = true;
            SetState(stCall);
        }
    }

    // 捕球した。fly=ノーバウンド (アウト)
    static void FielderCaught(int i, bool fly)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        var f = fs[i];
        f.CatchPose = CatchPose(i, new Vec3(bx, by, bz));
        ballHeldBy = i;
        f.Anim = fly ? AnReach : AnReady;
        f.AnimT = 0;
        if (fly)
        {
            outs++;
            ShowEvent("CAUGHT!", Color.Rgb(1.0f, 0.6f, 0.3f));
            // 打者アウト。走者は帰塁 (簡略: その場から戻る)
            var br = batterRunner;
            if (br != null)
            {
                retiredRunner = br;
                rns.Remove(br);
            }
            batterRunner = null;
            foreach (var r in rns)
                r.To = r.AtBase;
            playPhase = plSettle;
            stateT = 0;
            return;
        }
        // ゴロ/落ちたフライ: 一塁封殺が間に合いそうなら送球、無理ならヒット確定
        var gatherDist = (float)Math.Sqrt(f.X * f.X + f.Z * f.Z);
        var brg = batterRunner;
        if (brg != null && brg.AtBase < 1 && gatherDist < 34)
        {
            f.Anim = AnThrow;
            f.AnimT = 0;
            playPhase = plThrow1b;
            throwFromX = bx;
            throwFromY = Math.Max(by, 1.3f);
            throwFromZ = bz;
            var d = (float)Math.Sqrt((baseD - bx) * (baseD - bx)
                + (baseD - bz) * (baseD - bz));
            throwDur = Math.Max(0.25f, d / 30.0f);
            throwT = 0.0f;
            return;
        }
        // ヒット: 深さと経過時間で進塁数を決める
        var bases = 1;
        if (gatherDist > 62 || liveT > 4.6f)
            bases = 2;
        if (gatherDist > 72 && liveT > 5.5f)
            bases = 3;
        ShowEvent(bases == 1 ? "HIT!" : bases == 2 ? "DOUBLE!" : "TRIPLE!",
            Color.Rgb(0.55f, 1.0f, 0.6f));
        foreach (var r in rns)
            r.To = r == batterRunner ? bases : Math.Min(4, r.AtBase + bases);
        batterRunner = null;
        playPhase = plSettle;
        stateT = 0;
    }

    static void UpdateRunners(float dt, float spdScale)
    {
        var rns = runners;
        if (rns == null)
            return;
        int i = rns.Count - 1;
        while (i >= 0)
        {
            var r = rns[i];
            if (r.AtBase < r.To)
            {
                int nextBase = r.AtBase + 1;
                var np = BasePos(nextBase == 4 ? 0 : nextBase);
                var dx = np[0] - r.X;
                var dz = np[1] - r.Z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = runSpd * spdScale * dt;
                if (d <= mv)
                {
                    r.X = np[0];
                    r.Z = np[1];
                    r.AtBase = nextBase;
                    if (nextBase >= 4)
                    {
                        score[BattingTeam()] = score[BattingTeam()] + 1;
                        ShowEvent("RUN SCORED!", Color.Rgb(1.0f, 0.9f, 0.4f));
                        rns.RemoveAt(i);
                    }
                }
                else
                {
                    r.X += dx / d * mv;
                    r.Z += dz / d * mv;
                }
                r.RunPhase += Math.Min(d, mv) / runSpd * 11.0f;
            }
            else if (r.AtBase > r.To)
            {
                // 帰塁
                var np = BasePos(r.To);
                var dx = np[0] - r.X;
                var dz = np[1] - r.Z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = runSpd * dt;
                if (d <= mv)
                {
                    r.X = np[0];
                    r.Z = np[1];
                    r.AtBase = r.To;
                }
                else
                {
                    r.X += dx / d * mv;
                    r.Z += dz / d * mv;
                }
                r.RunPhase += Math.Min(d, mv) / runSpd * 11.0f;
            }
            i--;
        }
    }

    // --- state machine 本体 ----------------------------------------------------------
    static void UpdateGame(float dt)
    {
        stateT += dt;
        eventT += dt;
        var fs = fielders;
        var rns = runners;
        var b = batter;
        if (fs == null || rns == null || b == null)
            return;
        if (state == stIntro)
        {
            if (stateT > 1.8f)
            {
                ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
                SetState(stPrepitch);
            }
        }
        else if (state == stPrepitch)
        {
            // 全員が定位置に戻るのを待つ (テンポ優先で上限 1.4s)
            for (int i = 0; i < 9; i++)
            {
                var f = fs[i];
                if (i != 1)
                    MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8f);
            }
            UpdateRunners(dt, 1.0f);
            if (newBatterPending)
            {
                b.X = -0.85f;
                b.Z = 0.0f;
                b.Anim = AnIdle;
                batterRunner = null;
                retiredRunner = null;
                batterAtPlate = true;
                balls = 0;
                strikes = 0;
                newBatterPending = false;
            }
            // 走者が塁に着くまでは投げない (四球の押し出し等)。上限つき
            var settled = true;
            foreach (var r in rns)
                if (r.AtBase != r.To)
                    settled = false;
            if (stateT > 1.05f && (settled || stateT > 6.0f))
            {
                if (outs >= 3)
                {
                    SetState(stChange);
                    ShowEvent("CHANGE", Color.Rgb(0.9f, 0.9f, 0.95f));
                }
                else
                {
                    StartPitch();
                }
            }
        }
        else if (state == stWindup)
        {
            fs[0].AnimT += dt / 1.1f;
            if (fs[0].AnimT >= RelPh)
                ReleaseBall();
        }
        else if (state == stPitch)
        {
            fs[0].AnimT = Math.Min(1.0f, fs[0].AnimT + dt / 1.1f);
            // 投球は無抵抗の放物線 (短距離なので誤差は無視できる)
            float contactZ = willSwing && swingOutcome != 0 ? 0.42f : -2.0f;
            float travel = Math.Min(dt, Math.Max(0, (contactZ - bz) / bvz));
            bx += bvx * travel;
            by += bvy * travel - 0.5f * grav * travel * travel;
            bz += bvz * travel;
            bvy -= grav * travel;
            if (by < ballR)
            {
                by = ballR;
                bvy = Math.Abs(bvy) * 0.30f;
            }
            float toCatcher = Math.Max(0, (-2.0f - bz) / bvz);
            fs[1].X = MathUtil.Lerp(fs[1].X, bx + bvx * toCatcher, Math.Min(1, dt * 12));
            // スイング開始タイミング (ミートの瞬間に SWING_HIT_PH が来るよう逆算)
            var tToPlate = bvz != 0 ? (0.42f - bz) / bvz : 0.0f;
            if (willSwing && !swingStarted && tToPlate < SwingHitPh * 0.55f)
            {
                b.Anim = AnSwing;
                b.AnimT = 0;
                swingStarted = true;
            }
            if (b.Anim == AnSwing)
                b.AnimT = MathUtil.Clamp(SwingHitPh - tToPlate / 0.55f, 0, 1);
            if (travel < dt || bz <= contactZ)
                ResolveContact();
        }
        else if (state == stLive)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0f, b.AnimT + dt / 0.55f);
                if (b.AnimT >= 1.0f)
                    b.Anim = AnIdle;
            }
            foreach (var f in fs)
                if (f.Anim == AnThrow || f.Anim == AnReach)
                    f.AnimT = Math.Min(1.0f, f.AnimT + dt / 0.45f);
            UpdateLive(dt);
        }
        else if (state == stCall)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0f, b.AnimT + dt / 0.55f);
                if (b.AnimT >= 1.0f)
                    b.Anim = AnIdle;
            }
            UpdateRunners(dt, 1.0f);
            if (stateT > 0.95f)
                SetState(stPrepitch);
        }
        else if (state == stChange)
        {
            if (stateT > 1.8f)
            {
                runners = new List<Runner>();
                outs = 0;
                balls = 0;
                strikes = 0;
                if (half == 1)
                {
                    inning++;
                    half = 0;
                }
                else
                {
                    half = 1;
                }
                // 3回終了 (かつ同点でない) で試合終了。延長は 5 回まで
                var over = (inning > 3 && score[0] != score[1]) || inning > 5;
                if (over)
                {
                    SetState(stEnd);
                    if (score[0] == score[1])
                        ShowEvent("DRAW", Color.Rgb(0.9f, 0.9f, 0.95f));
                    else
                    {
                        var w = score[0] > score[1] ? 0 : 1;
                        ShowEvent("GAME SET  " + teamName[w] + " WINS!",
                            Color.Rgb(1.0f, 0.9f, 0.4f));
                    }
                }
                else
                {
                    ResetActors();
                    newBatterPending = true;
                    SetState(stPrepitch);
                }
            }
        }
        else if (state == stEnd)
        {
            if (stateT > 5.0f)
            {
                // 新しい試合を自動で始める
                inning = 1;
                half = 0;
                score = new List<int> { 0, 0 };
                outs = 0;
                balls = 0;
                strikes = 0;
                ResetActors();
                newBatterPending = true;
                ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
                SetState(stPrepitch);
            }
        }
    }

    // --- カメラ ---------------------------------------------------------------------
    static Vec3? camEye = null;
    static Vec3? camTarget = null;
    static float camFov = 34.0f;
    static bool fieldView = false;
    static bool showAllBases = false;
    static bool firstBaseView = false;

    static void UpdateCamera(float dt)
    {
        var eye = camEye;
        var tgt = camTarget;
        if (eye == null || tgt == null)
            return;
        var de = new Vec3(2.8f, 2.6f, -5.2f);
        var dtg = new Vec3(-0.1f, 1.1f, 5);
        var dfov = 46.0f;
        if (state == stPrepitch && batterAtPlate && outs < 3)
        {
            de = new Vec3(2.1f, 1.5f, 3.6f);
            dtg = new Vec3(-0.65f, 1.1f, 0);
            dfov = 37;
        }
        var wide = state == stLive && playPhase != plFoul && liveT > 0.18f
            || state == stCall && fieldView;
        if (!wide)
            firstBaseView = false;
        if (wide && state == stLive && playPhase == plThrow1b && !firstBaseView)
        {
            var br = batterRunner;
            var cover = fielders![firstBaseCover];
            var points = new List<Vec3> {
                new Vec3(throwFromX, 0, throwFromZ),
                new Vec3(baseD, 0, baseD),
                new Vec3(cover.X, 0, cover.Z),
                new Vec3(br != null ? br.X : baseD, 0, br != null ? br.Z : baseD)
            };
            float left = baseD, right = baseD, front = baseD, back = baseD;
            foreach (var p in points)
            {
                left = Math.Min(left, p.X);
                right = Math.Max(right, p.X);
                front = Math.Min(front, p.Z);
                back = Math.Max(back, p.Z);
            }
            dtg = new Vec3((left + right) * 0.5f, 1.1f, (front + back) * 0.5f);
            // 送球の始点・走者・受け手を収め、リリース前から判定まで同じ構図を保つ。
            float distance = 7;
            foreach (var p in points)
            {
                float x = p.X - dtg.X, z = p.Z - dtg.Z;
                for (int h = 0; h < 2; h++)
                {
                    float y = h * 3.5f - dtg.Y;
                    float depth = x * 0.575f + y * 0.287f - z * 0.766f;
                    distance = Math.Max(distance, depth + (Math.Abs(x * 0.8f + z * 0.6f) + 1) / 0.61f);
                    distance = Math.Max(distance, depth + Math.Abs(-x * 0.172f + y * 0.958f + z * 0.230f) / 0.30f);
                }
            }
            de = dtg + new Vec3(0.575f, 0.287f, -0.766f) * distance;
            dfov = 44;
            firstBaseView = true;
        }
        else if (wide && firstBaseView)
        {
            de = eye;
            dtg = tgt;
            dfov = camFov;
        }
        else if (wide)
        {
            var land = landing;
            float lx = land != null ? MathUtil.Clamp(land.X, -55, 55) : 0;
            float lz = land != null ? MathUtil.Clamp(land.Z, 0, fenceR) : baseD;
            float left = Math.Min(-4, lx - 5);
            float right = Math.Max(baseD + 4, lx + 5);
            float back = Math.Max(baseD + 4, lz + 6);
            if (showAllBases)
            {
                left = Math.Min(left, -baseD - 4);
                back = Math.Max(back, baseD * 2 + 4);
            }
            float span = Math.Max(28, Math.Max((back + 4) * 1.05f, (right - left) * 0.9f));
            dtg = new Vec3((left + right) * 0.5f, 0, (back - 4) * 0.5f + 4);
            de = new Vec3(dtg.X + 6, span * 0.85f, dtg.Z - span * 0.9f);
            dfov = 50;
        }
        fieldView = wide;
        camEye = de;
        camTarget = dtg;
        camFov = dfov;
    }

    // --- 描画 -----------------------------------------------------------------------
    static bool reloaded = true; // hot reload で true に戻る (19_sdf と同じトリック)
    static FixedStep? step = null;

    static Renderer3d? ren = null;

    static float FielderYaw(int i)
    {
        var f = fielders![i];
        if (f.Anim == AnThrow)
        {
            float turn = (float)Math.Atan2(baseD - f.X, baseD - f.Z) - f.Yaw;
            turn = (float)Math.Atan2(Math.Sin(turn), Math.Cos(turn));
            return f.Yaw + turn * MathUtil.Smoothstep(0, 0.20f, f.AnimT);
        }
        if (f.Anim == AnRun || f.Anim == AnThrow || (i == chaser && state == stLive) || (state == stLive && (ballHeldBy == i || (i == firstBaseCover && playPhase == plThrow1b)))) return f.Yaw;
        if (i == 0) return (float)Math.PI;
        if (i == 1) return 0;
        return (float)Math.Atan2(-f.X, -f.Z);
    }

    static BaseballPose FielderPose(int i)
    {
        var f = fielders![i];
        if (i == 1)
        {
            var crouch = PoseCrouch(tAccum);
            if (f.CatchPose != null && ballHeldBy == i)
                return BlendPose(f.CatchPose, crouch, f.Anim == AnReach ? MathUtil.Smoothstep(0.10f, 0.45f, f.AnimT) : 1);
            if (state == stPitch && bvz < 0)
            {
                float remaining = Math.Max(0, (-2.0f - bz) / bvz);
                var target = new Vec3(bx + bvx * remaining, Math.Max(ballR, by + bvy * remaining - 0.5f * grav * remaining * remaining), -2.0f);
                var receive = CatchPose(i, target);
                return BlendPose(crouch, receive, MathUtil.Smoothstep(0, 0.20f, stateT));
            }
            return crouch;
        }
        if (i == chaser && state == stLive && playPhase == plFly && by < 2.0f
            && (f.X - bx) * (f.X - bx) + (f.Z - bz) * (f.Z - bz) < catchR * catchR)
            return CatchPose(i, new Vec3(bx, by, bz));
        if (f.CatchPose != null && ballHeldBy == i)
        {
            if (f.Anim == AnThrow) return BlendPose(f.CatchPose, PoseThrow(f.AnimT), MathUtil.Smoothstep(0, 0.20f, f.AnimT));
            return f.CatchPose;
        }
        return PoseFor(f.Anim,
            f.Anim == AnWindup || f.Anim == AnThrow || f.Anim == AnReach ? f.AnimT : tAccum,
            f.RunPhase);
    }

    static BaseballPose CatchPose(int i, Vec3 target)
    {
        var f = fielders![i];
        var hand = Mat4.RotateY(-FielderYaw(i)).MulPoint(target - new Vec3(f.X, 0, f.Z)) - new Vec3(0, 0.04f, 0.09f);
        var p = i == 1 ? PoseCrouch(tAccum) : PoseReady(0);
        if (i == 1)
        {
            float low = 1 - MathUtil.Smoothstep(0.10f, 0.75f, hand.Y);
            float high = MathUtil.Smoothstep(0.85f, 1.50f, hand.Y);
            p.Drop = -0.43f - 0.31f * low + 0.15f * high;
            p.Lean = 0.28f + 0.35f * low;
            p.HeadPitch = -p.Lean - 0.15f * high;
            p.RightHand = new Vec3(0.18f, 0.85f + p.Drop * 0.70f, 0.12f);
        }
        else
        {
            p.Drop = MathUtil.Clamp(hand.Y - 1.15f, -0.60f, 0);
            p.Lean = hand.Y < 0.5f ? 1.10f : 0.28f;
            p.HeadPitch = hand.Y > 1.5f ? -0.60f : -p.Lean;
            p.RightHand = new Vec3(0.20f, 1.0f + p.Drop * 0.4f, 0.30f);
        }
        p.LeftHand = hand;
        return p;
    }

    static Vec3 FielderBall(int i)
    {
        var f = fielders![i];
        return (Mat4.Translate(new Vec3(f.X, 0, f.Z)) * Mat4.RotateY(FielderYaw(i)))
            .MulPoint(MakeRig(FielderPose(i)).Ball);
    }

    static void DrawChar(float x, float z, float yaw, int team,
        BaseballPose pose, bool glove = false, bool holdingBall = false)
    {
        var renNow = ren;
        var cm = charMesh;
        if (renNow == null || cm == null)
            return;
        var vp = renNow.ViewProj;
        if (vp != null)
        {
            var clip = vp * new Vec4(x, 1, z, 1);
            if (clip.W <= 0 || Math.Abs(clip.X) > clip.W + 5 || Math.Abs(clip.Y) > clip.W + 5)
                return;
        }
        var model = Mat4.Translate(new Vec3(x, 0, z)) * Mat4.RotateY(yaw);
        var rig = MakeRig(pose);
        renNow.Draw(cm[team], model, new Draw3dOpts { Bones = PackRig(rig, cm[team].Data) });
        if (glove && gloveMesh != null)
            renNow.Draw(gloveMesh, model * Mat4.Translate(rig.LeftHand));
        if (holdingBall)
            renNow.Draw(ballMesh, model * Mat4.Translate(rig.Ball));
    }

    static Mat4 BatMatrix(float ph)
    {
        var b = batter;
        return Mat4.Translate(new Vec3(b != null ? b.X : 0, 0, b != null ? b.Z : 0))
            * Mat4.RotateY((float)Math.PI / 2) * BatLocalMatrix(ph);
    }

    // --- HUD ------------------------------------------------------------------------
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static Text? mtext = null;
    static SpriteBatch? hud = null;

    static bool EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null)
            return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new Text("bb24_text", fontPath, 32, 256);
            hud = new SpriteBatch(w, h, "bb24_hud", "bb24_hud");
        }
        return mtext != null;
    }

    static void HudText(string text, float x, float y, float size, Color color, bool center = false)
    {
        var mt = mtext;
        var batch = hud;
        if (mt == null || batch == null) return;
        float scale = size / mt.Px;
        mt.Draw(batch, text, center ? x - mt.Width(text, scale) * 0.5f : x, y, color, scale);
    }

    static void DrawHud()
    {
        if (!EnsureText()) return;
        var batch = hud;
        if (batch == null) return;
        var cream = Color.Hex(0xFFF3DB);
        var ink = Color.Rgb(0.04f, 0.10f, 0.15f, 0.95f);
        var gold = Color.Hex(0xF5C46B);
        batch.Begin();
        batch.Rect(20, 16, 616, 52, ink);
        batch.Rect(20, 16, 5, 52, Color.Hex(teamRgb[BattingTeam()]));
        var occupied = new bool[] { false, false, false };
        var rns = runners;
        if (rns != null)
            foreach (var runner in rns)
            {
                if (runner.AtBase >= 1 && runner.AtBase <= 3) occupied[runner.AtBase - 1] = true;
            }
        for (int i = 0; i < 3; i++)
        {
            float x = i == 0 ? 612 : i == 1 ? 596 : 580;
            float y = i == 1 ? 31 : 47;
            batch.Rect(x - 5, y - 5, 10, 10, occupied[i] ? gold : Color.Hex(0x607380));
        }
        HudText(teamName[0] + "  " + score[0] + " : " + score[1] + "  " + teamName[1], 38, 50, 24, cream);
        HudText((half == 0 ? "TOP " : "BOT ") + inning, 295, 48, 18, gold);
        HudText("B " + balls + "   S " + strikes + "   O " + outs, 380, 48, 18, cream);
        HudText("F2  MODEL VIEW", 748, 44, 16, cream);
        batch.Flush();
        if (eventText != "" && eventT < 1.6f && eventCol != null)
        {
            float a = MathUtil.Clamp((1.6f - eventT) / 0.3f, 0, 1);
            float size = Math.Min(28, 380 / Math.Max(1, mtext!.Width(eventText, 1.0f)) * mtext.Px);
            batch.Begin();
            batch.Rect(280, h - 64, 400, 44, Color.Rgb(0.04f, 0.10f, 0.15f, 0.93f * a));
            batch.Rect(280, h - 64, 3, 44, Color.Rgb(eventCol.R, eventCol.G, eventCol.B, a));
            HudText(eventText, w * 0.5f, h - 32, size,
                Color.Rgb(cream.R, cream.G, cream.B, a), true);
            batch.Flush();
        }
    }

    static void SimulateTick()
    {
        tAccum += tickDt;
        // ヒットストップ: その間シミュレーションだけ止める
        if (hitstopT > 0)
        {
            hitstopT -= tickDt;
        }
        else
        {
            UpdateGame(tickDt);
        }
        UpdateCamera(tickDt);

        var fs = fielders;
        if (fs == null) return;
        if (fs[1].Anim == AnReach)
        {
            fs[1].AnimT += tickDt;
            if (fs[1].AnimT > 0.5f)
                fs[1].Anim = AnCrouch;
        }
        else
        {
            fs[1].Anim = AnCrouch;
        }
    }

    static bool modelDebug = false;
    static bool debugKeyHeld = false;
    static bool debugViewControls = false;
    static int debugAnim = AnSwing;
    static float debugTime = 0.165f;
    static bool debugPlaying = false;
    static float debugSpeed = 0.25f;
    static float debugYaw = 90;
    static float debugOrbit = 35;
    static float debugElevation = 12;
    static float debugDistance = 5.5f;
    static bool debugProps = true;
    static bool debugGuides = true;
    static int debugTeam = 0;
    static Mesh3d? debugBox = null;
    static List<string> debugClips = new List<string> {
        "Idle", "Ready", "Run", "Pitch", "Swing", "High catch", "Crouch", "Throw", "Bind pose"
    };

    static float DebugDuration()
    {
        if (debugAnim == AnSwing) return 0.55f;
        if (debugAnim == AnWindup) return 1.1f;
        if (debugAnim == AnThrow || debugAnim == AnReach) return 0.45f;
        if (debugAnim == AnRun) return 2 * (float)Math.PI / 11;
        return 2 * (float)Math.PI / 2.1f;
    }

    static void DebugGuide(Mat4 transform, Vec3 origin, Color color)
    {
        var r = ren;
        if (r == null) return;
        r.Draw(debugBox, transform * Mat4.Translate(origin + new Vec3(0, 0, 0.55f))
            * Mat4.Scale(new Vec3(0.025f, 0.025f, 1.1f)), new Draw3dOpts { Tint = color });
        r.Draw(ballMesh, transform * Mat4.Translate(origin + new Vec3(0, 0, 1.1f))
            * Mat4.Scale(new Vec3(0.45f, 0.45f, 0.45f)), new Draw3dOpts { Tint = color });
    }

    static void DrawModelDebug(float dt)
    {
        var r = ren;
        var b = batter;
        if (r == null || b == null) return;
        if (debugBox == null)
        {
            debugBox = new Mesh3d("bb24_debug_box");
            var v = new List<float>();
            Shapes.Box(v, 0, 0, 0, 1, 1, 1, new List<float> { 1, 1, 1, 1 });
            debugBox.Rebuild(Shapes3d.FromInterleaved(v));
        }
        if (debugPlaying)
            debugTime = (debugTime + Math.Min(dt, 0.1f) * debugSpeed) % DebugDuration();
        Gfx.Size(out var debugWidth, out var debugHeight);
        Ui.SetNextWindow(12, 12, Math.Min(290, debugWidth - 24), Math.Min(332, debugHeight - 24));
        if (Ui.BeginWindow("Baseball model [F2]"))
        {
            Ui.Text("Match paused / same meshes and poses");
            if (Ui.Button("Return to match")) modelDebug = false;
            Ui.Separator();
            if (Ui.Button("Motion")) debugViewControls = false;
            Ui.SameLine();
            if (Ui.Button("View / display")) debugViewControls = true;
            if (!debugViewControls)
            {
                for (int i = 0; i < debugClips.Count; i++)
                {
                    if (i % 3 != 0) Ui.SameLine();
                    if (Ui.Button(debugClips[i]))
                    {
                        debugAnim = i;
                        debugTime = 0;
                        debugPlaying = false;
                        debugYaw = i == AnSwing ? 90 : i == AnWindup ? 180 : i == AnRun || i == AnThrow ? 45 : 0;
                    }
                }
                Ui.Text("Clip: " + debugClips[debugAnim]);
                debugPlaying = Ui.Checkbox("Play / loop", debugPlaying);
                Ui.SameLine();
                if (Ui.Button("-1 frame"))
                {
                    debugPlaying = false;
                    debugTime = Math.Max(0, debugTime - tickDt);
                }
                Ui.SameLine();
                if (Ui.Button("+1 frame"))
                {
                    debugPlaying = false;
                    debugTime = Math.Min(DebugDuration(), debugTime + tickDt);
                }
                debugSpeed = Ui.SliderFloat("Speed", debugSpeed, 0.05f, 1);
                float scrub = Ui.SliderFloat("Time (s)", debugTime, 0, DebugDuration());
                if (scrub != debugTime) debugPlaying = false;
                debugTime = scrub;
                if (debugAnim == AnSwing)
                {
                    if (Ui.Button("Stance")) { debugTime = 0.30f * 0.55f; debugPlaying = false; }
                    Ui.SameLine();
                    if (Ui.Button("Contact")) { debugTime = SwingHitPh * 0.55f; debugPlaying = false; }
                    Ui.SameLine();
                    if (Ui.Button("Follow-through")) { debugTime = 0.9f * 0.55f; debugPlaying = false; }
                }
                else if (debugAnim == AnWindup || debugAnim == AnThrow)
                {
                    if (Ui.Button("Release"))
                    {
                        debugTime = debugAnim == AnWindup ? RelPh * 1.1f : 0.24f;
                        debugPlaying = false;
                    }
                }
                Ui.Text("Yellow: chest / Cyan: face");
            }
            else
            {
                debugYaw = Ui.SliderFloat("Body yaw", debugYaw, 0, 360);
                if (Ui.Button("Front")) debugOrbit = debugYaw;
                Ui.SameLine();
                if (Ui.Button("Side")) debugOrbit = (debugYaw + 90) % 360;
                Ui.SameLine();
                if (Ui.Button("Back")) debugOrbit = (debugYaw + 180) % 360;
                debugOrbit = Ui.SliderFloat("Camera orbit", debugOrbit, 0, 360);
                debugElevation = Ui.SliderFloat("Elevation", debugElevation, -10, 80);
                debugDistance = Ui.SliderFloat("Distance", debugDistance, 3.5f, 10);
                debugTeam = Ui.SliderInt("Team", debugTeam, 0, 1);
                debugProps = Ui.Checkbox("Bat / glove", debugProps);
                debugGuides = Ui.Checkbox("Direction guides", debugGuides);
                if (debugGuides)
                {
                    Ui.Text("World: red +X / blue +Z");
                    Ui.Text("Root: white / Chest: yellow");
                    Ui.Text("Face: cyan / dots: forward");
                }
            }
        }
        Ui.EndWindow();

        float orbit = MathUtil.Radians(debugOrbit);
        float elevation = MathUtil.Radians(debugElevation);
        var side = new Vec3((float)Math.Cos(orbit), 0, -(float)Math.Sin(orbit));
        var target = new Vec3(0, 1.1f, 0) + side * (debugDistance * 0.20f);
        var eye = target + new Vec3((float)Math.Sin(orbit) * (float)Math.Cos(elevation),
            (float)Math.Sin(elevation), (float)Math.Cos(orbit) * (float)Math.Cos(elevation)) * debugDistance;
        r.Light.Dir = new Vec3(-0.65f, 1, 0.45f);
        r.Light.Intensity = 1.8f;
        r.Light.Color = Color.Rgb(1, 1, 1);
        r.Sky.Intensity = 0.7f;
        r.Background = Color.Rgb(0.19f, 0.22f, 0.26f);
        r.Ssao.Enabled = false;
        r.Bloom.Enabled = false;
        r.Vignette = 0;
        r.Shadow.Size = 512;
        r.Shadow.Center = new Vec3(0, 0, 0);
        r.Shadow.Extent = 6;
        r.Begin(new Camera { Eye = eye, Target = target, Fov = 38, Near = 0.1f, Far = 40 });
        r.Draw(debugBox, Mat4.Translate(new Vec3(0, -0.06f, 0)) * Mat4.Scale(new Vec3(12, 0.1f, 12)),
            new Draw3dOpts { Tint = Color.Rgb(0.27f, 0.29f, 0.31f) });
        float phase = debugTime / DebugDuration();
        var pose = debugAnim == 8 ? new BaseballPose { Bind = true } : PoseFor(debugAnim,
            debugAnim == AnWindup || debugAnim == AnSwing || debugAnim == AnThrow || debugAnim == AnReach
                ? phase : debugTime, debugTime * 11);
        float yaw = MathUtil.Radians(debugYaw);
        var root = Mat4.RotateY(yaw);
        bool held = debugProps && ((debugAnim == AnWindup && phase <= RelPh)
            || (debugAnim == AnThrow && debugTime <= 0.24f) || debugAnim == AnReach || debugAnim == AnCrouch);
        DrawChar(0, 0, yaw, debugTeam, pose, debugProps && debugAnim != AnSwing, held);
        if (debugProps && debugAnim == AnSwing)
        {
            var fromBatter = Mat4.RotateY(-(float)Math.PI / 2) * Mat4.Translate(new Vec3(-b.X, 0, -b.Z));
            r.Draw(batMesh, root * fromBatter * BatMatrix(phase));
        }
        if (debugGuides)
        {
            DebugGuide(Mat4.RotateY((float)Math.PI / 2), new Vec3(0, 0.03f, 0), Color.Rgb(1, 0.1f, 0.1f));
            DebugGuide(new Mat4(), new Vec3(0, 0.03f, 0), Color.Rgb(0.15f, 0.35f, 1));
            DebugGuide(root, new Vec3(0, 0.08f, 0), Color.Rgb(1, 1, 1));
            var packed = PackBones(pose);
            var bones = charMesh![debugTeam].Data!.Bones;
            if (bones != null)
                for (int i = 0; i < bones.Count; i++)
                {
                    var bone = bones[i];
                    if (bone.Name != "torso" && bone.Name != "head") continue;
                    var matrix = new Mat4();
                    for (int j = 0; j < 16; j++) matrix.M[j] = packed[i * 16 + j];
                    DebugGuide(root * matrix, new Vec3(bone.X, bone.Y, bone.Z),
                        bone.Name == "head" ? Color.Rgb(0, 1, 1) : Color.Rgb(1, 0.8f, 0));
                }
        }
        r.End();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Ui.Render();
        Gfx.EndPass();
    }

    // --- main loop --------------------------------------------------------------------
    public static void OnFrame(float dt)
    {
        if (reloaded)
        {
            rng = new Rand(0x0B5EBA11);
            ren = new Renderer3d("bb24");
            camEye = new Vec3(2.8f, 2.6f, -5.2f);
            camTarget = new Vec3(-0.1f, 1.1f, 5);
            camFov = 46;
            BuildCharMesh();
            BuildField();
            ResetActors();
            state = stIntro;
            stateT = 0;
            reloaded = false;
            ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
        }

        if (modelDebug)
        {
            DrawModelDebug(dt);
            return;
        }

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => SimulateTick());

        var fs = fielders;
        var renNow = ren;
        var eyeNow = camEye;
        var tgtNow = camTarget;
        if (fs == null || renNow == null || eyeNow == null || tgtNow == null)
            return;

        var t = tAccum;

        // --- 描画 ---
        // 屋外デーゲーム: 高い太陽 + 空色の環境光
        renNow.Light.Dir = new Vec3(-0.65f, 1.0f, 0.45f);
        renNow.Light.Intensity = 2.2f;
        renNow.Light.Color = Color.Rgb(1.0f, 0.88f, 0.70f);
        renNow.Sky.Top = Color.Rgb(0.43f, 0.61f, 0.85f);
        renNow.Sky.Bottom = Color.Rgb(0.22f, 0.28f, 0.20f);
        renNow.Sky.Intensity = 0.7f;
        renNow.Background = Color.Rgb(0.57f, 0.73f, 0.83f);
        renNow.Ssao.Enabled = false;
        renNow.Bloom.Enabled = false;
        renNow.Vignette = 0;
        renNow.Shadow.Size = 512;
        // 影はカメラターゲット周辺 (フィールド全体 100m は 1 枚に入れない)
        renNow.Shadow.Center = new Vec3(tgtNow.X, 0, tgtNow.Z);
        renNow.Shadow.Extent = 30.0f;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = tgtNow,
            Fov = camFov,
            Near = 0.3f,
            Far = 180.0f,
        });

        renNow.Draw(fieldMesh, new Mat4());

        // 野手 (守備側チーム色)
        var ft = FieldingTeam();
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            var pose = FielderPose(i);
            var yaw = FielderYaw(i);
            var holding = ballVisible && ballHeldBy == i
                && (state != stLive || playPhase != plThrow1b || throwT < 0.24f);
            DrawChar(f.X, f.Z, yaw, ft, pose, true, holding);
        }
        // 打者 (攻撃側チーム色)。走者に切り替わっていない間だけ打席に立つ
        var bt = BattingTeam();
        var b = batter;
        if (batterAtPlate && b != null)
        {
            // 構え = スイングの溜め位相を静止で使う (バットの持ち手と一致する)
            var stance = b.Anim == AnSwing ? b.AnimT : 0.30f;
            var inSwingPose = b.Anim == AnSwing
                || state == stPrepitch
                || state == stWindup
                || state == stPitch
                || state == stCall;
            DrawChar(b.X, b.Z, (float)Math.PI / 2, bt,
                inSwingPose ? PoseSwing(stance) : PoseIdle(t));
            // バット
            if (state == stPrepitch || state == stWindup || state == stPitch
                || state == stCall || b.Anim == AnSwing)
                renNow.Draw(batMesh, BatMatrix(stance));
        }
        // 走者 (塁上で止まっているときは待機ポーズ)
        var rns = runners;
        if (rns != null)
        {
            foreach (var r in rns)
            {
                var np = BasePos(r.To == 4 ? 0 : r.To);
                var moving = r.AtBase != r.To;
                var yaw = moving ? (float)Math.Atan2(np[0] - r.X, np[1] - r.Z)
                    : (float)Math.Atan2(-r.X, -r.Z);
                var pose = moving ? PoseRun(r.RunPhase) : PoseIdle(t);
                if (r == batterRunner && b != null && liveT < 0.24f)
                {
                    float blend = MathUtil.Smoothstep(0.10f, 0.24f, liveT);
                    var swing = PoseSwing(b.AnimT);
                    pose = BlendPose(swing, pose, blend);
                    yaw = MathUtil.Lerp((float)Math.PI / 2, yaw, blend);
                    if (liveT < 0.10f)
                        renNow.Draw(batMesh, Mat4.Translate(new Vec3(r.X - b.X, 0, r.Z - b.Z)) * BatMatrix(b.AnimT));
                }
                DrawChar(r.X, r.Z, yaw, bt, pose);
            }
        }
        var retired = retiredRunner;
        if (retired != null)
            DrawChar(retired.X, retired.Z, (float)Math.Atan2(baseD - retired.X, baseD - retired.Z),
                bt, PoseIdle(t));

        // ボール
        if (ballVisible && !(ballHeldBy >= 0
            && (state != stLive || playPhase != plThrow1b || throwT < 0.24f)))
        {
            var delta = new Vec3(bx - eyeNow.X, by - eyeNow.Y, bz - eyeNow.Z);
            float scale = Math.Max(1, delta.Length() * 0.03f);
            renNow.Draw(ballMesh, Mat4.Translate(new Vec3(bx, by + ballR * (scale - 1), bz))
                * Mat4.Scale(new Vec3(scale, scale, scale)));
        }

        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        Gfx.EndPass();
    }
}

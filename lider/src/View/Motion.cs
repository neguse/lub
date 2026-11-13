// 操作の手応えに使う動き: 目標に spring で寄る値、窓の見た目の位置と大きさ、粒。
// どれも dt で進め、入力を待たせない (次の目標が来れば、今の動きのまま追いかける)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

// 目標へ寄る値。
// 減衰は臨界より少し弱い (減衰比 0.67) ので、軽く行き過ぎてから収まる
public class Spring
{
    public float Value;
    public float Velocity;
    public float Stiffness = 180;
    public float Damping = 18;

    public Spring(float value)
    {
        Value = value;
    }

    public void Step(float target, float dt)
    {
        // 大きい dt でも暴れないよう、1/120 秒ずつ進める
        while (dt > 0)
        {
            var h = Math.Min(dt, 1.0f / 120);
            var a = Stiffness * (target - Value) - Damping * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public void Snap(float value)
    {
        Value = value;
        Velocity = 0;
    }
}

public static class Ease
{
    // v を target へ、0 から 1 の幅を seconds で進む速さで寄せる (行き過ぎない)
    public static float Toward(float v, float target, float dt, float seconds)
    {
        var step = dt / seconds;
        return v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    }
}

// 窓の見た目。
// 開いたときは小さな大きさから広がり、閉じるとその場で縮んで消える
public class WindowMotion
{
    // 開く前と閉じた後の大きさ
    public const float SmallScale = 0.15f;
    public readonly Spring Angle;
    public readonly Spring Y;
    public readonly Spring Scale;
    // つかんでいる間は 1 (手前へ浮く)
    public readonly Spring Lift = new Spring(0);
    // focus のある間は 1 (縁が光る)
    public readonly Spring Glow = new Spring(0);
    // 閉じた後、縮みきるまで描く
    public bool Closing;
    public float ClosingFor;
    public const float CloseSeconds = 0.35f;
    // 保存して反映が届いた時に、枠が揺れる強さと、飛んだ先の行の印 (どれも 1 から 0 へ減る)
    public float Shake;
    public float Arrive;
    // 保存した時刻と、反映にかかった秒とそれを見出しに出す残りの秒
    public float SavedAt;
    public float LandSeconds;
    public float LandLabel;
    // 保存の波 (1 から 0 へ減る) と、打った行の光と、compile error の赤い光
    public float Wave;
    public float Pulse;
    public float Alarm;
    // 行が面に沈み終えた所から広がる波 (1 から 0 へ減る) と、その真ん中 (view の中の px)
    public float Ripple;
    public float RippleAt;
    // cursor の見た目 (x は view の中の px、y は本文の先頭からの px)。
    // 打つ速さに追いつくよう、行き過ぎずに 0.2 秒ほどで収まる
    public readonly Spring CaretX = Caret();
    public readonly Spring CaretY = Caret();
    public bool CaretShown;
    // 前の frame の cursor (行と byte の桁) と、その行の byte の長さと、cursor の x
    public int TypedLine;
    public int TypedCol;
    public int TypedLength;
    public float TypedX;
    // Seen* を取った file (タブを替えると変わる)
    public Document? SeenDoc;
    public int SeenRemoved;
    public int SeenSaved;
    public int SeenFailures;
    public int SeenLanded;
    public int SeenRevision;

    public WindowMotion(float angle, float y, float scale)
    {
        Angle = new Spring(angle);
        Y = new Spring(y);
        Scale = new Spring(scale);
    }

    public void Step(float angle, float y, float scale, bool lifted, bool focused, float dt)
    {
        Angle.Step(angle, dt);
        Y.Step(y, dt);
        Scale.Step(scale, dt);
        Lift.Step(lifted ? 1 : 0, dt);
        Glow.Step(focused ? 1 : 0, dt);
        Shake = Math.Max(0, Shake - dt / 0.3f);
        Arrive = Math.Max(0, Arrive - dt / 0.4f);
        LandLabel = Math.Max(0, LandLabel - dt);
        Wave = Math.Max(0, Wave - dt / 0.45f);
        Pulse = Math.Max(0, Pulse - dt / 0.25f);
        Alarm = Math.Max(0, Alarm - dt / 0.8f);
        Ripple = Math.Max(0, Ripple - dt / 0.6f);
        if (Closing)
            ClosingFor += dt;
    }

    public bool Gone => Closing && ClosingFor >= CloseSeconds;

    static Spring Caret()
    {
        var s = new Spring(0);
        s.Stiffness = 1600;
        s.Damping = 80;
        return s;
    }
}

// 打った文字 (Line 行目の X0 から X1 の px、view の中)。
// 窓の texture の同じ所を、手前に浮かせて大きめに描き、Seconds で面に収める
public class Landing
{
    public const float Seconds = 0.2f;
    public CodeWindow Window;
    public int Line;
    public float X0;
    public float X1;
    public float Age;

    public Landing(CodeWindow window, int line, float x0, float x1)
    {
        Window = window;
        Line = line;
        X0 = x0;
        X1 = x1;
    }
}

// 粒 (円筒の角度、高さ、半径と、その速さ)
public class Particle
{
    public float Angle;
    public float Y;
    public float Radius;
    public float VAngle;
    public float VY;
    public float VRadius;
    public float Life;
    public float MaxLife;
    public Color Color;
    // 大きさの倍率
    public float Size = 1;

    public Particle(float angle, float y, float radius, Color color, float life)
    {
        Angle = angle;
        Y = y;
        Radius = radius;
        Color = color;
        Life = life;
        MaxLife = life;
    }
}

// 着弾の所から広がって消える四角い輪 (発光でなく、くっきりした線)
public class Ring
{
    public const float Seconds = 0.32f;
    public float Angle;
    public float Y;
    public float Size;
    public float Age;
    public Color Color = Color.Rgb(1, 1, 1);
}

public class Particles
{
    public readonly List<Particle> Live = new List<Particle>();
    int seed = 12345;

    // 0 から 1 の擬似乱数 (同じ順に同じ値)
    public float Next()
    {
        seed = seed * 1664525 + 1013904223;
        return (float)((seed >> 8) & 0xffff) / 65535.0f;
    }

    // (angle, y, radius) から n 個、速さ speed (m/s) の範囲で散らす。
    // toward は半径の向きの速さ (負なら見る人の方へ)
    public void Burst(float angle, float y, float radius, int n, float speed, float toward, Color color, float life, float size = 1)
    {
        for (var i = 0; i < n; i++)
        {
            var p = new Particle(angle, y, radius, color, life * (0.6f + 0.4f * Next()));
            p.Size = size;
            p.VAngle = (Next() - 0.5f) * speed / radius;
            p.VY = (Next() - 0.3f) * speed;
            p.VRadius = toward * (0.5f + Next());
            Live.Add(p);
        }
    }

    public void Step(float dt)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            var p = Live[i];
            p.Life -= dt;
            if (p.Life <= 0)
            {
                Live.RemoveAt(i);
                continue;
            }
            p.Angle += p.VAngle * dt;
            p.Y += p.VY * dt;
            p.Radius += p.VRadius * dt;
            // 落ちながら遅くなる
            p.VY -= 0.6f * dt;
            p.VAngle *= 1 - 2 * dt;
        }
    }
}

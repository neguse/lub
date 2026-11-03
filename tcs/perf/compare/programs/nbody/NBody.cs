using System;

// float の演算と object の field。The Computer Language Benchmarks Game の n-body を float (f32) で計算する
public class Body
{
    public float X, Y, Z, Vx, Vy, Vz, Mass;

    public Body(float x, float y, float z, float vx, float vy, float vz, float mass)
    {
        X = x;
        Y = y;
        Z = z;
        Vx = vx;
        Vy = vy;
        Vz = vz;
        Mass = mass;
    }
}

public class NBody
{
    static float Energy(Body[] bodies)
    {
        float e = 0.0f;
        for (int i = 0; i < bodies.Length; i++)
        {
            Body b = bodies[i];
            e = e + 0.5f * b.Mass * (b.Vx * b.Vx + b.Vy * b.Vy + b.Vz * b.Vz);
            for (int j = i + 1; j < bodies.Length; j++)
            {
                Body c = bodies[j];
                float dx = b.X - c.X;
                float dy = b.Y - c.Y;
                float dz = b.Z - c.Z;
                e = e - (b.Mass * c.Mass) / (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return e;
    }

    static void Advance(Body[] bodies, float dt)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            Body b = bodies[i];
            for (int j = i + 1; j < bodies.Length; j++)
            {
                Body c = bodies[j];
                float dx = b.X - c.X;
                float dy = b.Y - c.Y;
                float dz = b.Z - c.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                float mag = dt / (d2 * (float)Math.Sqrt(d2));
                float bm = c.Mass * mag;
                float cm = b.Mass * mag;
                b.Vx = b.Vx - dx * bm;
                b.Vy = b.Vy - dy * bm;
                b.Vz = b.Vz - dz * bm;
                c.Vx = c.Vx + dx * cm;
                c.Vy = c.Vy + dy * cm;
                c.Vz = c.Vz + dz * cm;
            }
        }
        for (int i = 0; i < bodies.Length; i++)
        {
            Body b = bodies[i];
            b.X = b.X + dt * b.Vx;
            b.Y = b.Y + dt * b.Vy;
            b.Z = b.Z + dt * b.Vz;
        }
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 1000 : int.Parse(s);
        float pi = 3.14159265f;
        float solarMass = 4.0f * pi * pi;
        float daysPerYear = 365.24f;

        var bodies = new Body[5];
        bodies[0] = new Body(0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, solarMass);
        bodies[1] = new Body(4.84143144f, -1.16032004f, -0.103622044f,
            0.00166007664f * daysPerYear, 0.00769901118f * daysPerYear, -0.0000690460016f * daysPerYear,
            0.000954791938f * solarMass);
        bodies[2] = new Body(8.34336671f, 4.12479856f, -0.403523417f,
            -0.00276742510f * daysPerYear, 0.00499852801f * daysPerYear, 0.0000230417297f * daysPerYear,
            0.000285885980f * solarMass);
        bodies[3] = new Body(12.8943695f, -15.1111514f, -0.223307579f,
            0.00296460137f * daysPerYear, 0.00237847173f * daysPerYear, -0.0000296589568f * daysPerYear,
            0.0000436624404f * solarMass);
        bodies[4] = new Body(15.3796971f, -25.9193146f, 0.179258772f,
            0.00268067772f * daysPerYear, 0.00162824170f * daysPerYear, -0.0000951592254f * daysPerYear,
            0.0000515138902f * solarMass);

        float px = 0.0f, py = 0.0f, pz = 0.0f;
        for (int i = 0; i < bodies.Length; i++)
        {
            px = px + bodies[i].Vx * bodies[i].Mass;
            py = py + bodies[i].Vy * bodies[i].Mass;
            pz = pz + bodies[i].Vz * bodies[i].Mass;
        }
        bodies[0].Vx = -px / solarMass;
        bodies[0].Vy = -py / solarMass;
        bodies[0].Vz = -pz / solarMass;

        Console.WriteLine("energy before: " + (int)(Energy(bodies) * 1000000.0f));
        for (int i = 0; i < n; i++) Advance(bodies, 0.01f);
        Console.WriteLine("energy after: " + (int)(Energy(bodies) * 1000000.0f));
    }
}

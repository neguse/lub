using System.Collections.Generic;

// Stub declarations for the b2 Lua C module.
// At runtime, this gets overwritten by: b2 = require("b2")
public class b2
{
    public static int STATIC = 0;
    public static int KINEMATIC = 1;
    public static int DYNAMIC = 2;

    public static object createWorld(float gx, float gy) { return null; }
    public static void destroyWorld(object w) { }
    public static object createBody(object w, int type, float x, float y) { return null; }
    public static void addBox(object body, float hw, float hh, float density, float friction, float restitution) { }
    public static void worldStep(object w, float dt, int substeps) { }
    public static float getPositionX(object body) { return 0; }
    public static float getPositionY(object body) { return 0; }
}

public class Body
{
    public string Label;
    public object Handle;

    public Body(string label, object handle)
    {
        Label = label;
        Handle = handle;
    }
}

public class Simulation
{
    public object World;
    public List<Body> Bodies = new List<Body>();
    public List<string> Log = new List<string>();

    public void Init()
    {
        World = b2.createWorld(0, -10);

        // Static ground at y=-1
        var ground = b2.createBody(World, b2.STATIC, 0, -1);
        b2.addBox(ground, 50, 1, 0, 0.3f, 0);

        // 3 dynamic boxes at different heights
        var box1 = b2.createBody(World, b2.DYNAMIC, 0, 10);
        b2.addBox(box1, 0.5f, 0.5f, 1, 0.3f, 0.2f);
        Bodies.Add(new Body("BoxA", box1));

        var box2 = b2.createBody(World, b2.DYNAMIC, 0.8f, 20);
        b2.addBox(box2, 0.5f, 0.5f, 1, 0.3f, 0.5f);
        Bodies.Add(new Body("BoxB", box2));

        var box3 = b2.createBody(World, b2.DYNAMIC, -0.3f, 30);
        b2.addBox(box3, 0.5f, 0.5f, 1, 0.3f, 0.8f);
        Bodies.Add(new Body("BoxC", box3));
    }

    public string Snapshot()
    {
        string line = "";
        for (int i = 0; i < Bodies.Count; i++)
        {
            var body = Bodies[i];
            var y = b2.getPositionY(body.Handle);
            if (i > 0) { line = line + "  "; }
            line = line + body.Label + ":y=" + y.ToString();
        }
        return line;
    }

    public void Step(float dt, int substeps)
    {
        b2.worldStep(World, dt, substeps);
    }

    public void Cleanup()
    {
        b2.destroyWorld(World);
    }
}

public class Demo
{
    public static string Run()
    {
        var sim = new Simulation();
        sim.Init();

        sim.Log.Add("=== Box2D v3 + TinyC# Headless Physics ===");
        sim.Log.Add("3 boxes dropping under gravity (0,-10)");
        sim.Log.Add("Simulating 3s at 60Hz...");
        sim.Log.Add("");

        int totalFrames = 180;
        int printEvery = 30;

        for (int frame = 1; frame <= totalFrames; frame++)
        {
            sim.Step(1.0f / 60.0f, 4);

            if (frame % printEvery == 0)
            {
                var t = frame / 60.0f;
                sim.Log.Add("t=" + t.ToString() + "s: " + sim.Snapshot());
            }
        }

        sim.Log.Add("");
        sim.Log.Add("Final: " + sim.Snapshot());

        sim.Cleanup();
        sim.Log.Add("Done.");

        // Join all lines
        string result = "";
        for (int i = 0; i < sim.Log.Count; i++)
        {
            if (i > 0) { result = result + "\n"; }
            result = result + sim.Log[i];
        }
        return result;
    }
}

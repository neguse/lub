// Float math and object fields: n-body in float (f32)
class Body {
    float x, y, z, vx, vy, vz, mass;

    Body(float x, float y, float z, float vx, float vy, float vz, float mass) {
        this.x = x;
        this.y = y;
        this.z = z;
        this.vx = vx;
        this.vy = vy;
        this.vz = vz;
        this.mass = mass;
    }
}

public class NBody {
    static float energy(Body[] bodies) {
        float e = 0.0f;
        for (int i = 0; i < bodies.length; i++) {
            Body b = bodies[i];
            e = e + 0.5f * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz);
            for (int j = i + 1; j < bodies.length; j++) {
                Body c = bodies[j];
                float dx = b.x - c.x;
                float dy = b.y - c.y;
                float dz = b.z - c.z;
                e = e - (b.mass * c.mass) / (float) Math.sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return e;
    }

    static void advance(Body[] bodies, float dt) {
        for (int i = 0; i < bodies.length; i++) {
            Body b = bodies[i];
            for (int j = i + 1; j < bodies.length; j++) {
                Body c = bodies[j];
                float dx = b.x - c.x;
                float dy = b.y - c.y;
                float dz = b.z - c.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                float mag = dt / (d2 * (float) Math.sqrt(d2));
                float bm = c.mass * mag;
                float cm = b.mass * mag;
                b.vx = b.vx - dx * bm;
                b.vy = b.vy - dy * bm;
                b.vz = b.vz - dz * bm;
                c.vx = c.vx + dx * cm;
                c.vy = c.vy + dy * cm;
                c.vz = c.vz + dz * cm;
            }
        }
        for (int i = 0; i < bodies.length; i++) {
            Body b = bodies[i];
            b.x = b.x + dt * b.vx;
            b.y = b.y + dt * b.vy;
            b.z = b.z + dt * b.vz;
        }
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 1000 : Integer.parseInt(s);
        float pi = 3.14159265f;
        float solarMass = 4.0f * pi * pi;
        float daysPerYear = 365.24f;

        Body[] bodies = new Body[5];
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
        for (int i = 0; i < bodies.length; i++) {
            px = px + bodies[i].vx * bodies[i].mass;
            py = py + bodies[i].vy * bodies[i].mass;
            pz = pz + bodies[i].vz * bodies[i].mass;
        }
        bodies[0].vx = -px / solarMass;
        bodies[0].vy = -py / solarMass;
        bodies[0].vz = -pz / solarMass;

        System.out.println("energy before: " + (int) (energy(bodies) * 1000000.0f));
        for (int i = 0; i < n; i++) advance(bodies, 0.01f);
        System.out.println("energy after: " + (int) (energy(bodies) * 1000000.0f));
    }
}

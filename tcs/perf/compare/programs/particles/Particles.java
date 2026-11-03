// Game loop: particle array with gravity, bounces and a pull toward the centre
// Java has no value types, so the C# struct array becomes an array of objects created up front
class Particle {
    float px, py, vx, vy;
}

public class Particles {
    static int state = 12345;

    static int lcg() {
        state = (state * 1103515245 + 12345) & 0x3FFFFFFF;
        return state;
    }

    static float frand(float lo, float hi) {
        return lo + (lcg() % 1000) * (1.0f / 1000.0f) * (hi - lo);
    }

    static int run(int frames, boolean print) {
        state = 12345;
        int n = 4096;
        float dt = 0.02f;

        Particle[] ps = new Particle[n];
        for (int i = 0; i < n; i++) ps[i] = new Particle();
        for (int i = 0; i < n; i++) {
            ps[i].px = frand(100.0f, 300.0f);
            ps[i].py = frand(50.0f, 200.0f);
            ps[i].vx = frand(-40.0f, 40.0f);
            ps[i].vy = frand(-40.0f, 40.0f);
        }

        for (int f = 0; f < frames; f++) {
            for (int i = 0; i < n; i++) {
                ps[i].vy = ps[i].vy + 98.0f * dt;
                ps[i].px = ps[i].px + ps[i].vx * dt;
                ps[i].py = ps[i].py + ps[i].vy * dt;
                if (ps[i].px < 0.0f) { ps[i].px = 0.0f; ps[i].vx = -ps[i].vx * 0.9f; }
                if (ps[i].px > 400.0f) { ps[i].px = 400.0f; ps[i].vx = -ps[i].vx * 0.9f; }
                if (ps[i].py < 0.0f) { ps[i].py = 0.0f; ps[i].vy = -ps[i].vy * 0.9f; }
                if (ps[i].py > 240.0f) { ps[i].py = 240.0f; ps[i].vy = -ps[i].vy * 0.9f; }
                float dx = ps[i].px - 200.0f;
                float dy = ps[i].py - 120.0f;
                float d2 = dx * dx + dy * dy;
                if (d2 < 400.0f) {
                    ps[i].px = ps[i].px + dx * 0.1f;
                    ps[i].py = ps[i].py + dy * 0.1f;
                }
            }
        }

        float sx = 0.0f;
        float sy = 0.0f;
        for (int i = 0; i < n; i++) {
            sx = sx + ps[i].px;
            sy = sy + ps[i].py;
        }
        if (print) System.out.println("sum x: " + (int) sx + ", sum y: " + (int) sy);
        return (int) sx + (int) sy;
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 100 : Integer.parseInt(s);
        // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
        String rs = System.getenv("BENCH_REPS");
        int reps = rs == null ? 1 : Integer.parseInt(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

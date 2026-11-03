import java.util.ArrayList;

// Game loop: class sprites in a list, moved every frame and bounced at the screen edges
class Sprite {
    float x, y, vx, vy;
    int frame;
}

public class Sprites {
    static int state = 12345;

    static int lcg() {
        state = (state * 1103515245 + 12345) & 0x3FFFFFFF;
        return state;
    }

    static float frand(float lo, float hi) {
        return lo + (lcg() % 1000) * (1.0f / 1000.0f) * (hi - lo);
    }

    static void update(Sprite s, float dt) {
        s.x = s.x + s.vx * dt;
        s.y = s.y + s.vy * dt;
        if (s.x < 0.0f) { s.x = 0.0f; s.vx = -s.vx; }
        if (s.x > 400.0f) { s.x = 400.0f; s.vx = -s.vx; }
        if (s.y < 0.0f) { s.y = 0.0f; s.vy = -s.vy; }
        if (s.y > 240.0f) { s.y = 240.0f; s.vy = -s.vy; }
        s.frame = (s.frame + 1) % 8;
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int frames = s == null ? 100 : Integer.parseInt(s);
        int count = 1024;
        float dt = 0.02f;

        ArrayList<Sprite> sprites = new ArrayList<>();
        for (int i = 0; i < count; i++) {
            Sprite sp = new Sprite();
            sp.x = frand(0.0f, 400.0f);
            sp.y = frand(0.0f, 240.0f);
            sp.vx = frand(-60.0f, 60.0f);
            sp.vy = frand(-60.0f, 60.0f);
            sp.frame = lcg() % 8;
            sprites.add(sp);
        }

        for (int f = 0; f < frames; f++) {
            for (Sprite sp : sprites) update(sp, dt);
        }

        float sx = 0.0f;
        float sy = 0.0f;
        int sf = 0;
        for (Sprite sp : sprites) {
            sx = sx + sp.x;
            sy = sy + sp.y;
            sf = sf + sp.frame;
        }
        System.out.println("sum x: " + (int) sx + ", sum y: " + (int) sy + ", sum frame: " + sf);
    }
}

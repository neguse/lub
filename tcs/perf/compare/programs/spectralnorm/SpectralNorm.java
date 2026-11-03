// Float multiply-add in a double loop with int division: spectral-norm in float (f32)
public class SpectralNorm {
    static float a(int i, int j) {
        int ij = i + j;
        return 1.0f / (ij * (ij + 1) / 2 + i + 1);
    }

    static void multiplyAv(int n, float[] v, float[] av) {
        for (int i = 0; i < n; i++) {
            float sum = 0.0f;
            for (int j = 0; j < n; j++) sum = sum + a(i, j) * v[j];
            av[i] = sum;
        }
    }

    static void multiplyAtv(int n, float[] v, float[] atv) {
        for (int i = 0; i < n; i++) {
            float sum = 0.0f;
            for (int j = 0; j < n; j++) sum = sum + a(j, i) * v[j];
            atv[i] = sum;
        }
    }

    static void multiplyAtAv(int n, float[] v, float[] tmp, float[] atav) {
        multiplyAv(n, v, tmp);
        multiplyAtv(n, tmp, atav);
    }

    static int run(int n, boolean print) {
        float[] u = new float[n];
        float[] v = new float[n];
        float[] tmp = new float[n];
        for (int i = 0; i < n; i++) u[i] = 1.0f;
        for (int k = 0; k < 10; k++) {
            multiplyAtAv(n, u, tmp, v);
            multiplyAtAv(n, v, tmp, u);
        }
        float vBv = 0.0f;
        float vv = 0.0f;
        for (int i = 0; i < n; i++) {
            vBv = vBv + u[i] * v[i];
            vv = vv + v[i] * v[i];
        }
        int result = (int) ((float) Math.sqrt(vBv / vv) * 1000000.0f);
        if (print) System.out.println("spectral norm x 1e6: " + result);
        return result;
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

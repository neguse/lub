using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private CType? RuntimeServiceResult(IlCall call)
    {
        if (call.Callee == "os.getenv")
        {
            RequireArity(call.Callee, call.Args.Length, 1);
            RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
            return CType.String;
        }
        if (call.Callee == "TinySystem.Random.Next")
        {
            RequireArity(call.Callee, call.Args.Length, 0);
            return CType.I32;
        }
        return null;
    }

    private string RenderRuntimeService(IlCall call, CType result) => RenderOrderedCall(
        call.Callee == "os.getenv" ? "tcs_getenv" : "tcs_random_next", result,
        call.Args.Select(a => (TypeOf(a), RenderExpr(a))).ToList());

    private const string RuntimeServices = """
        #include <time.h>

        static TcsString *tcs_getenv(TcsString *name)
        {
            tcs_nonnull(name);
            if (name->length == SIZE_MAX) tcs_fault("allocation-overflow");
            char *key = malloc(name->length + 1);
            if (key == NULL) tcs_fault("allocation");
            memcpy(key, name->data, name->length);
            key[name->length] = 0;
            const char *value = getenv(key);
            free(key);
            return value == NULL ? NULL : tcs_string_new((const unsigned char *)value, strlen(value));
        }
        static int32_t tcs_random_next(void)
        {
            static uint32_t state;
            if (state == 0) {
                state = (uint32_t)time(NULL) ^ (uint32_t)clock();
                if (state == 0) state = UINT32_C(0x9e3779b9);
            }
            uint32_t result;
            do {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                result = state & UINT32_C(0x7fffffff);
            } while (result == INT32_MAX);
            return (int32_t)result;
        }

        """;
}

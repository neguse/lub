namespace TinyCs.Tcs2c;

// C runtime の library 節: string.format、Math (Lua 意味論)、List sort
// (安定 merge sort、比較は生成側の lifted 関数)、Dict.Keys / Values、
// Console.Write、os.getenv。
internal sealed partial class CEmitter
{
    private const string RuntimeLib = """
        /* ---- string.format ---- */
        enum { TCS_FMT_I32, TCS_FMT_F32, TCS_FMT_BOOL, TCS_FMT_STR };
        typedef struct TcsFormatArg {
            int kind;
            int32_t i;
            float f;
            bool b;
            TcsString *s;
        } TcsFormatArg;

        static void
        tcs_format_append(unsigned char **buffer, size_t *length, size_t *capacity,
            const unsigned char *data, size_t count)
        {
            if (*length + count > *capacity) {
                unsigned char *grown;
                while (*length + count > *capacity) *capacity *= 2;
                grown = realloc(*buffer, *capacity);
                if (grown == NULL) tcs_fault("allocation");
                *buffer = grown;
            }
            memcpy(*buffer + *length, data, count);
            *length += count;
        }

        static TcsString *
        tcs_format_arg_string(const TcsFormatArg *arg)
        {
            switch (arg->kind) {
            case TCS_FMT_I32: return tcs_string_i32(arg->i);
            case TCS_FMT_F32: return tcs_string_f32(arg->f);
            case TCS_FMT_BOOL: return tcs_string_bool(arg->b);
            default: return tcs_string_tostring(arg->s);
            }
        }

        /* Lua の string.format: flags [-+ #0]、幅・精度は 2 桁まで。
           %d は整数、%f/%e/%g は double 昇格、%s は tostring、%c は byte */
        static TcsString *
        tcs_string_format(TcsString *format, size_t argc, const TcsFormatArg *argv)
        {
            size_t capacity = 64;
            size_t length = 0;
            unsigned char *buffer = malloc(capacity);
            size_t i = 0;
            size_t next = 0;
            TcsString *result;
            tcs_nonnull(format);
            if (buffer == NULL) tcs_fault("allocation");
            while (i < format->length) {
                char spec[32];
                size_t speclen = 0;
                char out[512];
                int written = 0;
                const TcsFormatArg *arg;
                unsigned char c = format->data[i];
                if (c != '%') {
                    tcs_format_append(&buffer, &length, &capacity, &c, 1);
                    i++;
                    continue;
                }
                i++;
                if (i < format->length && format->data[i] == '%') {
                    tcs_format_append(&buffer, &length, &capacity, (const unsigned char *)"%", 1);
                    i++;
                    continue;
                }
                spec[speclen++] = '%';
                while (i < format->length && strchr("-+ #0", format->data[i]) != NULL
                       && speclen < 8)
                    spec[speclen++] = (char)format->data[i++];
                while (i < format->length && format->data[i] >= '0' && format->data[i] <= '9'
                       && speclen < 12)
                    spec[speclen++] = (char)format->data[i++];
                if (i < format->length && format->data[i] == '.') {
                    spec[speclen++] = (char)format->data[i++];
                    while (i < format->length && format->data[i] >= '0'
                           && format->data[i] <= '9' && speclen < 16)
                        spec[speclen++] = (char)format->data[i++];
                }
                if (i >= format->length) tcs_fault("invalid format string");
                c = format->data[i++];
                if (next >= argc) tcs_fault("bad argument to 'format' (no value)");
                arg = &argv[next++];
                switch (c) {
                case 'd': case 'i': {
                    long long value;
                    if (arg->kind == TCS_FMT_I32) value = arg->i;
                    else if (arg->kind == TCS_FMT_F32 && arg->f == (float)(long long)arg->f)
                        value = (long long)arg->f;
                    else tcs_fault("number has no integer representation");
                    spec[speclen++] = 'l'; spec[speclen++] = 'l'; spec[speclen++] = 'd';
                    spec[speclen] = '\0';
                    written = snprintf(out, sizeof(out), spec, value);
                    break;
                }
                case 'x': case 'X': case 'o': case 'u': {
                    unsigned long long value;
                    if (arg->kind == TCS_FMT_I32) value = (uint32_t)arg->i;
                    else if (arg->kind == TCS_FMT_F32 && arg->f == (float)(long long)arg->f)
                        value = (uint32_t)(int32_t)arg->f;
                    else tcs_fault("number has no integer representation");
                    spec[speclen++] = 'l'; spec[speclen++] = 'l'; spec[speclen++] = (char)c;
                    spec[speclen] = '\0';
                    written = snprintf(out, sizeof(out), spec, value);
                    break;
                }
                case 'c': {
                    unsigned char ch;
                    if (arg->kind != TCS_FMT_I32) tcs_fault("number expected");
                    ch = (unsigned char)arg->i;
                    tcs_format_append(&buffer, &length, &capacity, &ch, 1);
                    continue;
                }
                case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A': {
                    double value;
                    if (arg->kind == TCS_FMT_I32) value = arg->i;
                    else if (arg->kind == TCS_FMT_F32) value = arg->f;
                    else tcs_fault("number expected");
                    spec[speclen++] = (char)c;
                    spec[speclen] = '\0';
                    written = snprintf(out, sizeof(out), spec, value);
                    break;
                }
                case 's': {
                    TcsString *text = tcs_format_arg_string(arg);
                    char *copy;
                    if (speclen == 1) {
                        tcs_format_append(&buffer, &length, &capacity, text->data, text->length);
                        continue;
                    }
                    copy = malloc(text->length + 1);
                    if (copy == NULL) tcs_fault("allocation");
                    memcpy(copy, text->data, text->length);
                    copy[text->length] = '\0';
                    spec[speclen++] = 's';
                    spec[speclen] = '\0';
                    written = snprintf(out, sizeof(out), spec, copy);
                    free(copy);
                    break;
                }
                default:
                    tcs_fault("invalid conversion to 'format'");
                }
                if (written < 0) tcs_fault("format");
                if ((size_t)written >= sizeof(out)) written = (int)sizeof(out) - 1;
                tcs_format_append(&buffer, &length, &capacity, (const unsigned char *)out,
                    (size_t)written);
            }
            result = tcs_string_new(buffer, length);
            free(buffer);
            return result;
        }

        /* ---- Math (TinySystem.Math の Lua 意味論) ---- */
        static int32_t
        tcs_trunc_f32(float value)
        {
            if (!(value > -2147483649.0f && value < 2147483648.0f))
                tcs_fault("float-to-int");
            return (int32_t)value;
        }

        static float
        tcs_math_log(float x, float base, int has_base)
        {
            if (!has_base) return TCS_MATH(logf)(x);
            if (base == 2.0f) return TCS_MATH(log2f)(x);
            if (base == 10.0f) return TCS_MATH(log10f)(x);
            return TCS_MATH(logf)(x) / TCS_MATH(logf)(base);
        }

        /* Lua の ^ (luai_numpow) と同じく、指数 2 は乗算 */
        static float
        tcs_math_pow(float x, float y)
        {
            return y == 2.0f ? x * x : TCS_MATH(powf)(x, y);
        }

        /* C# Math.Round (banker's)。digits 付きは runtime の Lua 実装を
           float 演算のまま写す */
        static float
        tcs_math_round(float x, int32_t digits, int has_digits)
        {
            float scale = has_digits ? tcs_math_pow(10.0f, (float)digits) : 1.0f;
            float scaled = x * scale;
            float fl = floorf(scaled);
            float diff = scaled - fl;
            float rounded;
            if (diff > 0.5f) rounded = fl + 1.0f;
            else if (diff < 0.5f) rounded = fl;
            else if (fmodf(fl, 2.0f) == 0.0f) rounded = fl;
            else rounded = fl + 1.0f;
            return has_digits ? rounded / scale : rounded;
        }

        /* ---- List sort (安定 merge sort。比較は生成側の lifted 関数) ---- */
        typedef int (*TcsCompare)(void *ctx, const void *left, const void *right);

        static void
        tcs_list_sort(TcsList *list, TcsCompare compare, void *ctx)
        {
            TcsList *scratch;
            size_t width;
            size_t n;
            size_t es;
            tcs_nonnull(list);
            n = list->length;
            es = list->element_size;
            if (n < 2) return;
            /* 作業領域は GC が trace できる List (比較 closure が確保しうる) */
            scratch = tcs_list_new(es, TCS_GC_HEADER(list)->layout);
            tcs_list_reserve(scratch, n);
            scratch->length = n;
            for (width = 1; width < n; width *= 2) {
                size_t lo;
                for (lo = 0; lo < n; lo += 2 * width) {
                    size_t mid = lo + width < n ? lo + width : n;
                    size_t hi = lo + 2 * width < n ? lo + 2 * width : n;
                    size_t a = lo, b = mid, k = lo;
                    unsigned char *src = list->data;
                    unsigned char *dst = scratch->data;
                    while (a < mid && b < hi) {
                        if (compare(ctx, src + b * es, src + a * es) < 0)
                            memcpy(dst + (k++) * es, src + (b++) * es, es);
                        else
                            memcpy(dst + (k++) * es, src + (a++) * es, es);
                    }
                    while (a < mid) memcpy(dst + (k++) * es, src + (a++) * es, es);
                    while (b < hi) memcpy(dst + (k++) * es, src + (b++) * es, es);
                }
                memcpy(list->data, scratch->data, n * es);
            }
        }

        static TcsList *
        tcs_list_copy(TcsList *list)
        {
            TcsList *copy;
            tcs_nonnull(list);
            copy = tcs_list_new(list->element_size, TCS_GC_HEADER(list)->layout);
            if (list->length != 0) {
                tcs_list_reserve(copy, list->length);
                memcpy(copy->data, list->data, list->length * list->element_size);
                copy->length = list->length;
            }
            return copy;
        }

        static TcsList *
        tcs_array_to_list(TcsArray *array)
        {
            TcsList *list;
            tcs_nonnull(array);
            list = tcs_list_new(array->element_size, TCS_GC_HEADER(array)->layout);
            if (array->length != 0) {
                tcs_list_reserve(list, array->length);
                memcpy(list->data, array->data, array->length * array->element_size);
                list->length = array->length;
            }
            return list;
        }

        /* ---- Dict.Keys / Values ---- */
        static TcsList *
        tcs_dict_keys(TcsDict *dict)
        {
            TcsList *keys;
            size_t b;
            tcs_nonnull(dict);
            keys = dict->key_is_string
                ? tcs_list_new(sizeof(TcsString *), &tcs_layout_ptr)
                : tcs_list_new(sizeof(int32_t), NULL);
            for (b = 0; b < dict->bucket_count; b++) {
                TcsDictNode *node;
                for (node = dict->buckets[b]; node != NULL; node = node->next) {
                    if (dict->key_is_string)
                        tcs_list_add(keys, &node->key_s, sizeof(node->key_s), &tcs_layout_ptr);
                    else
                        tcs_list_add(keys, &node->key_i, sizeof(node->key_i), NULL);
                }
            }
            return keys;
        }

        static TcsList *
        tcs_dict_values(TcsDict *dict)
        {
            TcsList *values;
            size_t b;
            const TcsLayout *layout;
            tcs_nonnull(dict);
            layout = TCS_GC_HEADER(dict)->layout;
            values = tcs_list_new(dict->value_size, layout);
            for (b = 0; b < dict->bucket_count; b++) {
                TcsDictNode *node;
                for (node = dict->buckets[b]; node != NULL; node = node->next)
                    tcs_list_add(values, node->value, dict->value_size, layout);
            }
            return values;
        }

        static void
        tcs_dict_clear(TcsDict *dict)
        {
            tcs_nonnull(dict);
            if (dict->bucket_count != 0)
                memset(dict->buckets, 0, dict->bucket_count * sizeof(*dict->buckets));
            dict->count = 0;
        }

        /* ---- Random: Lua 5.5 lmathlib の xoshiro256** を LUA_32BITS 構成
           (lua_Integer / lua_Unsigned = 32bit、lua_Number = float、FIGS = 24)
           のまま移植する。seed 固定時は Lua backend と同じ列になる ---- */
        /* TinySystem.Random の instance。GC object (pointer slot なし) で、
           Shared は static 1 個 (heap 外なので GC は触らない) */
        typedef struct TcsRandom { uint64_t s[4]; } TcsRandom;
        static const TcsLayout tcs_layout_random = { sizeof(TcsRandom), 0, NULL };
        static TcsRandom tcs_rand_shared_state;
        static int tcs_rand_shared_seeded;
        static uint32_t tcs_rand_auto_counter;

        static uint64_t
        tcs_rand_rotl(uint64_t x, int n)
        {
            return (x << n) | (x >> (64 - n));
        }

        static uint64_t
        tcs_rand_next(TcsRandom *r)
        {
            uint64_t *s = r->s;
            uint64_t s0 = s[0];
            uint64_t s1 = s[1];
            uint64_t s2 = s[2] ^ s0;
            uint64_t s3 = s[3] ^ s1;
            uint64_t res = tcs_rand_rotl(s1 * 5, 7) * 9;
            s[0] = s0 ^ s3;
            s[1] = s1 ^ s2;
            s[2] = s2 ^ (s1 << 17);
            s[3] = tcs_rand_rotl(s3, 45);
            return res;
        }

        /* math.randomseed(n1, n2): state = { n1, 0xff, n2, 0 }、先頭 16 値を捨てる */
        static void
        tcs_rand_seed(TcsRandom *r, uint32_t n1, uint32_t n2)
        {
            int i;
            r->s[0] = n1;
            r->s[1] = 0xff;
            r->s[2] = n2;
            r->s[3] = 0;
            for (i = 0; i < 16; i++) tcs_rand_next(r);
        }

        /* seed 未指定: 起動ごと・instance ごとに異なる (luai_makeseed 相当) */
        static uint32_t
        tcs_rand_auto_seed(void)
        {
            tcs_rand_auto_counter++;
            return (uint32_t)time(NULL) ^ (uint32_t)(uintptr_t)&tcs_rand_shared_state
                ^ (tcs_rand_auto_counter * UINT32_C(0x9E3779B1));
        }

        /* Random.Shared (初回参照時に seed する) */
        static TcsRandom *
        tcs_random_shared(void)
        {
            if (!tcs_rand_shared_seeded) {
                tcs_rand_seed(&tcs_rand_shared_state, tcs_rand_auto_seed(), 0);
                tcs_rand_shared_seeded = 1;
            }
            return &tcs_rand_shared_state;
        }

        static TcsRandom *
        tcs_random_new(uint32_t seed)
        {
            TcsRandom *r = tcs_new_object(&tcs_layout_random);
            tcs_rand_seed(r, seed, 0);
            return r;
        }

        static TcsRandom *
        tcs_random_new_auto(void)
        {
            return tcs_random_new(tcs_rand_auto_seed());
        }

        /* math.random(): 上位 24 bit から [0, 1) の float */
        static float
        tcs_rand_float(TcsRandom *r)
        {
            uint64_t x;
            int64_t sx;
            float res;
            x = tcs_rand_next(r);
            sx = (int64_t)(x >> 40);
            res = (float)sx * (0.5f / (float)(1 << 23));
            if (sx < 0) res += 1.0f;
            return res;
        }

        static uint32_t
        tcs_rand_project(TcsRandom *r, uint32_t ran, uint32_t n)
        {
            uint32_t lim = n;
            int sh;
            for (sh = 1; (lim & (lim + 1)) != 0; sh *= 2) lim |= (lim >> sh);
            while ((ran &= lim) > n) ran = (uint32_t)tcs_rand_next(r);
            return ran;
        }

        /* math.random(low, up): [low, up] の整数 (32bit 演算) */
        static int32_t
        tcs_rand_range(TcsRandom *r, int32_t low, int32_t up)
        {
            uint64_t rv;
            uint32_t p;
            rv = tcs_rand_next(r);
            if (low > up) tcs_fault("interval is empty");
            p = tcs_rand_project(r, (uint32_t)rv, (uint32_t)up - (uint32_t)low);
            return (int32_t)(p + (uint32_t)low);
        }

        /* ---- char (整数 code unit、ASCII 判定 / 変換。Lua runtime の Char と同じ表) ---- */
        static TcsString *
        tcs_string_from_byte(int32_t code)
        {
            unsigned char byte = (unsigned char)code;
            return tcs_string_new(&byte, 1);
        }

        static bool tcs_char_is_digit(int32_t c) { return c >= 48 && c <= 57; }
        static bool tcs_char_is_upper(int32_t c) { return c >= 65 && c <= 90; }
        static bool tcs_char_is_lower(int32_t c) { return c >= 97 && c <= 122; }
        static bool tcs_char_is_letter(int32_t c) { return tcs_char_is_upper(c) || tcs_char_is_lower(c); }
        static bool tcs_char_is_letter_or_digit(int32_t c) { return tcs_char_is_letter(c) || tcs_char_is_digit(c); }
        static bool tcs_char_is_space(int32_t c) { return c == 32 || (c >= 9 && c <= 13); }
        static int32_t tcs_char_to_upper(int32_t c) { return tcs_char_is_lower(c) ? c - 32 : c; }
        static int32_t tcs_char_to_lower(int32_t c) { return tcs_char_is_upper(c) ? c + 32 : c; }

        /* ---- Console.Write / Environment ---- */
        static void
        tcs_write_string(TcsString *value)
        {
            if (value == NULL) { fputs("nil", stdout); return; }
            if (value->length != 0) fwrite(value->data, 1, value->length, stdout);
        }

        static TcsString *
        tcs_getenv(TcsString *name)
        {
            char *copy;
            const char *value;
            TcsString *result;
            tcs_nonnull(name);
            copy = malloc(name->length + 1);
            if (copy == NULL) tcs_fault("allocation");
            memcpy(copy, name->data, name->length);
            copy[name->length] = '\0';
            value = getenv(copy);
            free(copy);
            if (value == NULL) return NULL;
            result = tcs_string_new((const unsigned char *)value, strlen(value));
            return result;
        }

        """;
}

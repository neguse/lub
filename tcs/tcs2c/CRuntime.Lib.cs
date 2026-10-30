namespace TinyCs.Tcs2c;

// C runtime の library 節: string.format、Math (Lua 意味論)、List sort
// (.NET と同じ introsort と OrderBy の安定 merge sort、比較は生成側の lifted 関数)、Dict.Keys / Values、
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
            /* i32 の範囲は [-2^31, 2^31)。-2147483649.0f は f32 で -2^31 に丸まる
               ので、下限は >= で比べる (NaN はどちらの比較も偽で fault) */
            if (!(value >= -2147483648.0f && value < 2147483648.0f))
                tcs_fault("float-to-int");
            return (int32_t)value;
        }

        static float
        tcs_math_log(float x, float base, int has_base)
        {
            if (!has_base) return tcs_logf(x);
            /* MathF.Log(x, base) の特殊値 (logf(x) / logf(base) では出ない) */
            if (base == 1.0f || (x != 1.0f && (base == 0.0f || base == INFINITY)))
                return NAN;
            if (base == 2.0f) return tcs_log2f(x);
            if (base == 10.0f) return tcs_log10f(x);
            return tcs_logf(x) / tcs_logf(base);
        }

        /* Lua の ^ (luai_numpow) と同じく、指数 2 は乗算 */
        static float
        tcs_math_pow(float x, float y)
        {
            return y == 2.0f ? x * x : tcs_powf(x, y);
        }

        /* float.Equals (EqualityComparer<float>.Default): == に加えて NaN 同士も
           等しい */
        static bool
        tcs_float_equals(float a, float b)
        {
            return a == b || (a != a && b != b);
        }

        /* MathF.Min / Max (IEEE 754:2019 minimum / maximum): NaN を伝播し、
           -0 を +0 より小さいとみなす */
        static float
        tcs_math_min(float a, float b)
        {
            if (a != b) return a != a || a < b ? a : b;
            return signbit(a) ? a : b;
        }

        static float
        tcs_math_max(float a, float b)
        {
            if (a != b) return a != a || a > b ? a : b;
            return signbit(b) ? a : b;
        }

        /* C# Math.Round (banker's)。digits 付きは runtime の Lua 実装を
           float 演算のまま写す。0 に丸めた結果は x の符号を持つ */
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
            rounded = copysignf(rounded, x);
            return has_digits ? rounded / scale : rounded;
        }

        /* ---- List sort ----
           Sort は .NET の List<T>.Sort と同じ introsort (dotnet/runtime の ArraySortHelper<T>) で、同値の要素の並びと比較の呼び出し列まで同じ。
           OrderBy は安定 merge sort。
           比較は生成側の lifted 関数 */
        typedef int (*TcsCompare)(void *ctx, const void *left, const void *right);

        typedef struct TcsSort {
            TcsCompare compare;
            void *ctx;
            size_t es;
            unsigned char *held; /* pivot、挿入中の要素、DownHeap の要素 */
            unsigned char *tmp;  /* swap 用 */
        } TcsSort;

        static void
        tcs_sort_swap(TcsSort *s, unsigned char *keys, ptrdiff_t i, ptrdiff_t j)
        {
            size_t es = s->es;
            memcpy(s->tmp, keys + i * es, es);
            memcpy(keys + i * es, keys + j * es, es);
            memcpy(keys + j * es, s->tmp, es);
        }

        static void
        tcs_sort_swap_if_greater(TcsSort *s, unsigned char *keys, ptrdiff_t i, ptrdiff_t j)
        {
            if (s->compare(s->ctx, keys + i * s->es, keys + j * s->es) > 0)
                tcs_sort_swap(s, keys, i, j);
        }

        static void
        tcs_sort_insertion(TcsSort *s, unsigned char *keys, ptrdiff_t n)
        {
            size_t es = s->es;
            ptrdiff_t i;
            for (i = 0; i < n - 1; i++) {
                ptrdiff_t j = i;
                memcpy(s->held, keys + (i + 1) * es, es);
                while (j >= 0 && s->compare(s->ctx, s->held, keys + j * es) < 0) {
                    memcpy(keys + (j + 1) * es, keys + j * es, es);
                    j--;
                }
                memcpy(keys + (j + 1) * es, s->held, es);
            }
        }

        static void
        tcs_sort_down_heap(TcsSort *s, unsigned char *keys, ptrdiff_t i, ptrdiff_t n)
        {
            size_t es = s->es;
            memcpy(s->held, keys + (i - 1) * es, es);
            while (i <= n >> 1) {
                ptrdiff_t child = 2 * i;
                if (child < n && s->compare(s->ctx, keys + (child - 1) * es, keys + child * es) < 0)
                    child++;
                if (!(s->compare(s->ctx, s->held, keys + (child - 1) * es) < 0)) break;
                memcpy(keys + (i - 1) * es, keys + (child - 1) * es, es);
                i = child;
            }
            memcpy(keys + (i - 1) * es, s->held, es);
        }

        static void
        tcs_sort_heap(TcsSort *s, unsigned char *keys, ptrdiff_t n)
        {
            ptrdiff_t i;
            for (i = n >> 1; i >= 1; i--) tcs_sort_down_heap(s, keys, i, n);
            for (i = n; i > 1; i--) {
                tcs_sort_swap(s, keys, 0, i - 1);
                tcs_sort_down_heap(s, keys, 1, i - 1);
            }
        }

        /* 比較が不整合だと走査が区間の外に出る。
           .NET はそこで例外 (IndexOutOfRangeException) を投げるので、同じ位置で fault にする */
        static ptrdiff_t
        tcs_sort_partition(TcsSort *s, unsigned char *keys, ptrdiff_t n)
        {
            size_t es = s->es;
            ptrdiff_t hi = n - 1;
            ptrdiff_t middle = hi >> 1;
            ptrdiff_t left = 0;
            ptrdiff_t right = hi - 1;
            tcs_sort_swap_if_greater(s, keys, 0, middle);
            tcs_sort_swap_if_greater(s, keys, 0, hi);
            tcs_sort_swap_if_greater(s, keys, middle, hi);
            memcpy(s->held, keys + middle * es, es);
            tcs_sort_swap(s, keys, middle, hi - 1);
            while (left < right) {
                do {
                    if (++left > hi) tcs_fault("Unable to sort because the IComparer.Compare() method returns inconsistent results");
                } while (s->compare(s->ctx, keys + left * es, s->held) < 0);
                do {
                    if (--right < 0) tcs_fault("Unable to sort because the IComparer.Compare() method returns inconsistent results");
                } while (s->compare(s->ctx, s->held, keys + right * es) < 0);
                if (left >= right) break;
                tcs_sort_swap(s, keys, left, right);
            }
            if (left != hi - 1) tcs_sort_swap(s, keys, left, hi - 1);
            return left;
        }

        static void
        tcs_sort_intro(TcsSort *s, unsigned char *keys, ptrdiff_t n, int depth)
        {
            while (n > 1) {
                ptrdiff_t p;
                if (n <= 16) {
                    if (n == 2) {
                        tcs_sort_swap_if_greater(s, keys, 0, 1);
                    } else if (n == 3) {
                        tcs_sort_swap_if_greater(s, keys, 0, 1);
                        tcs_sort_swap_if_greater(s, keys, 0, 2);
                        tcs_sort_swap_if_greater(s, keys, 1, 2);
                    } else {
                        tcs_sort_insertion(s, keys, n);
                    }
                    return;
                }
                if (depth == 0) {
                    tcs_sort_heap(s, keys, n);
                    return;
                }
                depth--;
                p = tcs_sort_partition(s, keys, n);
                tcs_sort_intro(s, keys + (p + 1) * s->es, n - p - 1, depth);
                n = p;
            }
        }

        /* list[start..] を並べる (比較なしの float は NaN を先頭に寄せた残り) */
        static void
        tcs_list_sort(TcsList *list, size_t start, TcsCompare compare, void *ctx)
        {
            TcsSort s;
            TcsList *scratch;
            size_t n;
            size_t m;
            int log2 = 0;
            tcs_nonnull(list);
            n = list->length - start;
            if (n < 2) return;
            /* 作業領域は GC が trace できる List (比較 closure が確保しうる) */
            scratch = tcs_list_new(list->element_size, TCS_GC_HEADER(list)->layout);
            tcs_list_reserve(scratch, 2);
            scratch->length = 2;
            s.compare = compare;
            s.ctx = ctx;
            s.es = list->element_size;
            s.held = scratch->data;
            s.tmp = (unsigned char *)scratch->data + s.es;
            for (m = n; m > 1; m >>= 1) log2++;
            tcs_sort_intro(&s, (unsigned char *)list->data + start * s.es, (ptrdiff_t)n, 2 * (log2 + 1));
        }

        /* 比較なしの float の Sort の前処理 (.NET の SortUtils.MoveNansToFront)。
           NaN の数を返す */
        static size_t
        tcs_list_move_nans_to_front(TcsList *list)
        {
            float *values;
            size_t left = 0;
            size_t i;
            tcs_nonnull(list);
            values = list->data;
            for (i = 0; i < list->length; i++) {
                if (values[i] != values[i]) {
                    float t = values[left];
                    values[left] = values[i];
                    values[i] = t;
                    left++;
                }
            }
            return left;
        }

        /* copy constructor `new List<T>(source)`: 要素を値で写す */
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

        /* seed 未指定: 起動ごと・instance ごとに異なる (時刻由来で予測できる。il-spec §13) */
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

        /* Random.Next(min, max): 上限は排他。空の範囲 (max == min) は .NET と同じく min */
        static int32_t
        tcs_rand_below(TcsRandom *r, int32_t min, int32_t max)
        {
            return tcs_rand_range(r, min, max == min ? min : max - 1);
        }

        /* ---- char (整数 code unit、ASCII 判定 / 変換。Lua runtime の Char と同じ表) ---- */
        static TcsString *
        tcs_string_from_byte(int32_t code)
        {
            unsigned char byte = (unsigned char)code;
            return tcs_string_new(&byte, 1);
        }

        /* Lua の Char.ConvertFromUtf32 と同じ。範囲外は同じ文言で fault */
        static TcsString *
        tcs_string_from_codepoint(int32_t code)
        {
            unsigned char bytes[4];
            size_t length;
            if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                tcs_fault("char.ConvertFromUtf32: not a Unicode scalar value");
            if (code < 0x80) {
                bytes[0] = (unsigned char)code;
                length = 1;
            } else if (code < 0x800) {
                bytes[0] = (unsigned char)(0xC0 | (code >> 6));
                bytes[1] = (unsigned char)(0x80 | (code & 0x3F));
                length = 2;
            } else if (code < 0x10000) {
                bytes[0] = (unsigned char)(0xE0 | (code >> 12));
                bytes[1] = (unsigned char)(0x80 | ((code >> 6) & 0x3F));
                bytes[2] = (unsigned char)(0x80 | (code & 0x3F));
                length = 3;
            } else {
                bytes[0] = (unsigned char)(0xF0 | (code >> 18));
                bytes[1] = (unsigned char)(0x80 | ((code >> 12) & 0x3F));
                bytes[2] = (unsigned char)(0x80 | ((code >> 6) & 0x3F));
                bytes[3] = (unsigned char)(0x80 | (code & 0x3F));
                length = 4;
            }
            return tcs_string_new(bytes, length);
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

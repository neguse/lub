namespace TinyCs.Tcs2c;

// C runtime の string 節: TinySystem.String (runtime/tinysystem.lua) と
// Lua 標準関数 (string.sub / byte / upper / lower / format、tonumber、
// utf8.codes) の C 実装。意味論は Lua backend に揃える (1-based の
// clamp、plain find、%s 空白、byte 列)。
internal sealed partial class CEmitter
{
    private const string RuntimeStrings = """
        /* ---- string ---- */
        static TcsString *
        tcs_string_from_bytes(const unsigned char *data, size_t length)
        {
            return tcs_string_new(data, length);
        }

        /* Lua string.sub の index 正規化 (1-based、負は末尾から、clamp) */
        static TcsString *
        tcs_string_sub(TcsString *s, int32_t i, int32_t j)
        {
            int64_t length;
            int64_t from = i;
            int64_t to = j;
            tcs_nonnull(s);
            length = (int64_t)s->length;
            if (from < 0) from = length + from + 1;
            if (from < 1) from = 1;
            if (to < 0) to = length + to + 1;
            if (to > length) to = length;
            if (from > to) return tcs_string_new(NULL, 0);
            return tcs_string_new(s->data + (from - 1), (size_t)(to - from + 1));
        }

        static TcsString *
        tcs_string_sub_open(TcsString *s, int32_t i)
        {
            return tcs_string_sub(s, i, -1);
        }

        /* Lua string.byte(s, i): 範囲外は値なし → fault */
        static int32_t
        tcs_string_byte(TcsString *s, int32_t i)
        {
            int64_t length;
            int64_t at = i;
            tcs_nonnull(s);
            length = (int64_t)s->length;
            if (at < 0) at = length + at + 1;
            if (at < 1 || at > length) tcs_fault("string-index");
            return s->data[at - 1];
        }

        static TcsString *
        tcs_string_map_case(TcsString *s, int upper)
        {
            TcsString *result;
            size_t i;
            tcs_nonnull(s);
            result = tcs_string_new(s->data, s->length);
            for (i = 0; i < result->length; i++) {
                unsigned char c = result->data[i];
                if (upper && c >= 'a' && c <= 'z') result->data[i] = (unsigned char)(c - 32);
                if (!upper && c >= 'A' && c <= 'Z') result->data[i] = (unsigned char)(c + 32);
            }
            return result;
        }

        /* plain find: s の offset from 以降で sub が現れる最初の位置 (byte、
           無ければ -1)。空 sub は from 自身 (Lua の string.find と同じ) */
        static int64_t
        tcs_string_find(TcsString *s, TcsString *sub, size_t from)
        {
            size_t i;
            if (from > s->length) return -1;
            if (sub->length == 0) return (int64_t)from;
            if (sub->length > s->length) return -1;
            for (i = from; i + sub->length <= s->length; i++)
                if (memcmp(s->data + i, sub->data, sub->length) == 0)
                    return (int64_t)i;
            return -1;
        }

        static bool
        tcs_string_contains(TcsString *s, TcsString *sub)
        {
            tcs_nonnull(s);
            tcs_nonnull(sub);
            return tcs_string_find(s, sub, 0) >= 0;
        }

        static int32_t
        tcs_string_index_of(TcsString *s, TcsString *value, int32_t start)
        {
            int64_t found;
            tcs_nonnull(s);
            tcs_nonnull(value);
            if (start < 0) start = 0;
            found = tcs_string_find(s, value, (size_t)start);
            return found < 0 ? -1 : (int32_t)found;
        }

        static TcsString *
        tcs_string_replace(TcsString *s, TcsString *old, TcsString *replacement)
        {
            size_t capacity;
            size_t length = 0;
            unsigned char *buffer;
            size_t pos = 0;
            TcsString *result;
            tcs_nonnull(s);
            tcs_nonnull(old);
            tcs_nonnull(replacement);
            if (old->length == 0) tcs_fault("oldValue cannot be empty");
            capacity = s->length + 16;
            buffer = malloc(capacity);
            if (buffer == NULL) tcs_fault("allocation");
            while (true) {
                int64_t found = tcs_string_find(s, old, pos);
                size_t copy = found < 0 ? s->length - pos : (size_t)found - pos;
                size_t need = length + copy + replacement->length;
                if (need > capacity) {
                    unsigned char *grown;
                    while (need > capacity) capacity *= 2;
                    grown = realloc(buffer, capacity);
                    if (grown == NULL) tcs_fault("allocation");
                    buffer = grown;
                }
                memcpy(buffer + length, s->data + pos, copy);
                length += copy;
                if (found < 0) break;
                memcpy(buffer + length, replacement->data, replacement->length);
                length += replacement->length;
                pos = (size_t)found + old->length;
            }
            result = tcs_string_new(buffer, length);
            free(buffer);
            return result;
        }

        static bool
        tcs_string_starts_with(TcsString *s, TcsString *prefix)
        {
            tcs_nonnull(s);
            tcs_nonnull(prefix);
            return prefix->length <= s->length
                && memcmp(s->data, prefix->data, prefix->length) == 0;
        }

        static bool
        tcs_string_ends_with(TcsString *s, TcsString *suffix)
        {
            tcs_nonnull(s);
            tcs_nonnull(suffix);
            if (suffix->length == 0) return true;
            return suffix->length <= s->length
                && memcmp(s->data + s->length - suffix->length, suffix->data,
                    suffix->length) == 0;
        }

        static int
        tcs_is_space(unsigned char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f'
                || c == '\r';
        }

        static TcsString *
        tcs_string_trim(TcsString *s)
        {
            size_t from = 0;
            size_t to;
            tcs_nonnull(s);
            to = s->length;
            while (from < to && tcs_is_space(s->data[from])) from++;
            while (to > from && tcs_is_space(s->data[to - 1])) to--;
            return tcs_string_new(s->data + from, to - from);
        }

        static TcsString *
        tcs_string_substring(TcsString *s, int32_t start, int32_t length)
        {
            tcs_nonnull(s);
            if (length < 0) return tcs_string_sub(s, start + 1, -1);
            if (start + 1 > start + length) return tcs_string_new(NULL, 0);
            return tcs_string_sub(s, start + 1, start + length);
        }

        static bool
        tcs_string_is_null_or_empty(TcsString *s)
        {
            return s == NULL || s->length == 0;
        }

        static int
        tcs_string_compare(TcsString *left, TcsString *right)
        {
            size_t n;
            int c;
            tcs_nonnull(left);
            tcs_nonnull(right);
            n = left->length < right->length ? left->length : right->length;
            c = n == 0 ? 0 : memcmp(left->data, right->data, n);
            if (c != 0) return c < 0 ? -1 : 1;
            return left->length < right->length ? -1
                : left->length > right->length ? 1 : 0;
        }

        /* String.Split: sep なし → 空白 1 文字区切り、sep が空 → {str}。
           C# の戻りは string[] なので array にして返す */
        static TcsArray *
        tcs_string_split(TcsString *s, TcsString *sep, int has_sep)
        {
            TcsList *result;
            size_t pos = 0;
            tcs_nonnull(s);
            result = tcs_list_new(sizeof(TcsString *), &tcs_layout_ptr);
            if (has_sep && (sep == NULL || sep->length == 0)) {
                tcs_list_add(result, &s, sizeof(s), &tcs_layout_ptr);
                return tcs_list_to_array(result);
            }
            while (true) {
                int64_t found = -1;
                size_t stop = 0;
                TcsString *piece;
                if (has_sep) {
                    found = tcs_string_find(s, sep, pos);
                    if (found >= 0) stop = (size_t)found + sep->length;
                } else {
                    size_t i;
                    for (i = pos; i < s->length; i++)
                        if (tcs_is_space(s->data[i])) { found = (int64_t)i; stop = i + 1; break; }
                }
                if (found < 0) {
                    piece = tcs_string_new(s->data + pos, s->length - pos);
                    tcs_list_add(result, &piece, sizeof(piece), &tcs_layout_ptr);
                    return tcs_list_to_array(result);
                }
                piece = tcs_string_new(s->data + pos, (size_t)found - pos);
                tcs_list_add(result, &piece, sizeof(piece), &tcs_layout_ptr);
                pos = stop;
            }
        }

        static TcsString *
        tcs_string_join_items(TcsString *sep, TcsString **items, size_t count)
        {
            size_t total = 0;
            size_t i;
            size_t at = 0;
            TcsString *result;
            tcs_nonnull(sep);
            for (i = 0; i < count; i++) {
                tcs_nonnull(items[i]);
                total += items[i]->length + (i > 0 ? sep->length : 0);
            }
            result = tcs_string_new(NULL, total);
            for (i = 0; i < count; i++) {
                if (i > 0) { memcpy(result->data + at, sep->data, sep->length); at += sep->length; }
                memcpy(result->data + at, items[i]->data, items[i]->length);
                at += items[i]->length;
            }
            return result;
        }

        static TcsString *
        tcs_string_join_list(TcsString *sep, TcsList *list)
        {
            tcs_nonnull(list);
            return tcs_string_join_items(sep, (TcsString **)list->data, list->length);
        }

        static TcsString *
        tcs_string_join_array(TcsString *sep, TcsArray *array)
        {
            tcs_nonnull(array);
            return tcs_string_join_items(sep, (TcsString **)array->data, array->length);
        }

        /* tonumber: 10 進 (前後空白可)。失敗は Lua の nil → 後続で fault */
        static bool
        tcs_parse_i32(TcsString *s, int32_t *out)
        {
            char buffer[64];
            char *end;
            long long value;
            tcs_nonnull(s);
            if (s->length >= sizeof(buffer)) return false;
            memcpy(buffer, s->data, s->length);
            buffer[s->length] = '\0';
            if (strlen(buffer) != s->length) return false;
            errno = 0;
            value = strtoll(buffer, &end, 10);
            while (*end != '\0' && tcs_is_space((unsigned char)*end)) end++;
            if (end == buffer || *end != '\0' || errno != 0) return false;
            if (value < INT32_MIN || value > INT32_MAX) return false;
            *out = (int32_t)value;
            return true;
        }

        static int32_t
        tcs_parse_i32_or_fault(TcsString *s)
        {
            int32_t value;
            if (!tcs_parse_i32(s, &value)) tcs_fault("number-format");
            return value;
        }

        static bool
        tcs_parse_f32(TcsString *s, float *out)
        {
            char buffer[128];
            char *end;
            tcs_nonnull(s);
            if (s->length >= sizeof(buffer)) return false;
            memcpy(buffer, s->data, s->length);
            buffer[s->length] = '\0';
            if (strlen(buffer) != s->length) return false;
            *out = strtof(buffer, &end);
            while (*end != '\0' && tcs_is_space((unsigned char)*end)) end++;
            return end != buffer && *end == '\0';
        }

        static float
        tcs_parse_f32_or_fault(TcsString *s)
        {
            float value;
            if (!tcs_parse_f32(s, &value)) tcs_fault("number-format");
            return value;
        }

        /* int.TryParse / float.TryParse: (found, value or fallback) */
        static bool
        tcs_try_parse_i32(TcsString *s, int32_t *out, int32_t fallback)
        {
            if (tcs_parse_i32(s, out)) return true;
            *out = fallback;
            return false;
        }

        static bool
        tcs_try_parse_f32(TcsString *s, float *out, float fallback)
        {
            if (tcs_parse_f32(s, out)) return true;
            *out = fallback;
            return false;
        }

        /* utf8.codes: 位置 *pos (byte) から 1 codepoint 読む。不正列は fault */
        static int32_t
        tcs_utf8_next(TcsString *s, size_t *pos)
        {
            size_t i = *pos;
            unsigned char c = s->data[i];
            int32_t code;
            size_t extra;
            if (c < 0x80) { code = c; extra = 0; }
            else if ((c & 0xE0) == 0xC0) { code = c & 0x1F; extra = 1; }
            else if ((c & 0xF0) == 0xE0) { code = c & 0x0F; extra = 2; }
            else if ((c & 0xF8) == 0xF0) { code = c & 0x07; extra = 3; }
            else tcs_fault("invalid UTF-8 code");
            for (size_t k = 1; k <= extra; k++) {
                unsigned char cc;
                if (i + k >= s->length) tcs_fault("invalid UTF-8 code");
                cc = s->data[i + k];
                if ((cc & 0xC0) != 0x80) tcs_fault("invalid UTF-8 code");
                code = (code << 6) | (cc & 0x3F);
            }
            *pos = i + extra + 1;
            return code;
        }

        """;
}

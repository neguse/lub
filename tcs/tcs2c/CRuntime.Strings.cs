namespace TinyCs.Tcs2c;

// C runtime の string 節: TinySystem.String (runtime/tinysystem.lua) と
// Lua 標準関数 (string.upper / lower / format、tonumber) の C 実装。
// 意味論は Lua backend に揃える (plain find、byte 列)。
internal sealed partial class CEmitter
{
    private const string RuntimeStrings = """
        /* ---- string ---- */
        static TcsString *
        tcs_string_from_bytes(const unsigned char *data, size_t length)
        {
            return tcs_string_new(data, length);
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

        static TcsString *
        tcs_string_replace(TcsString *s, TcsString *old, TcsString *replacement)
        {
            size_t capacity;
            size_t length = 0;
            unsigned char *buffer;
            size_t pos = 0;
            TcsString *result;
            size_t replacement_length;
            tcs_nonnull(s);
            tcs_nonnull(old);
            if (old->length == 0) tcs_fault("oldValue cannot be empty");
            /* null の置換後文字列は一致部分の削除 (.NET の String.Replace) */
            replacement_length = replacement == NULL ? 0 : replacement->length;
            capacity = s->length + 16;
            buffer = malloc(capacity);
            if (buffer == NULL) tcs_fault("allocation");
            while (true) {
                int64_t found = tcs_string_find(s, old, pos);
                size_t copy = found < 0 ? s->length - pos : (size_t)found - pos;
                size_t need = length + copy + replacement_length;
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
                if (replacement_length != 0)
                    memcpy(buffer + length, replacement->data, replacement_length);
                length += replacement_length;
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
            size_t from;
            size_t to;
            tcs_nonnull(s);
            tcs_utf8_trim(s->data, s->length, &from, &to);
            return tcs_string_new(s->data + from, to - from);
        }

        static bool
        tcs_string_is_null_or_empty(TcsString *s)
        {
            return s == NULL || s->length == 0;
        }

        /* .NET の ordinal (UTF-16 の code unit の順)。
           UTF-8 の byte の順は code point の順で、UTF-16 の順と違うのは U+10000 以上 (lead byte F0-F4、UTF-16 では surrogate D800-DBFF) と U+E000-U+FFFF (lead byte EE-EF) の間だけ。
           最初に異なる byte がその 2 つの lead byte のときだけ逆にする */
        static int
        tcs_string_compare(TcsString *left, TcsString *right)
        {
            size_t n;
            size_t i;
            tcs_nonnull(left);
            tcs_nonnull(right);
            n = left->length < right->length ? left->length : right->length;
            for (i = 0; i < n; i++) {
                unsigned char a = left->data[i];
                unsigned char b = right->data[i];
                if (a == b) continue;
                if (a >= 0xF0 && a <= 0xF4 && b >= 0xEE && b <= 0xEF) return -1;
                if (b >= 0xF0 && b <= 0xF4 && a >= 0xEE && a <= 0xEF) return 1;
                return a < b ? -1 : 1;
            }
            return left->length < right->length ? -1
                : left->length > right->length ? 1 : 0;
        }

        /* string.CompareOrdinal。値は 0 との比較だけで使う (TCS1001 CompareOrdinalValue) ので
           -1 / 0 / 1 を返す。null は他のどの string より小さい */
        static int32_t
        tcs_string_compare_ordinal(TcsString *left, TcsString *right)
        {
            if (left == NULL) return right == NULL ? 0 : -1;
            if (right == NULL) return 1;
            return tcs_string_compare(left, right);
        }

        /* String.Split: sep なし → 空白 (char.IsWhiteSpace と同じ集合) 1 文字区切り、sep が空 → {str}。
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
                    size_t start;
                    if (tcs_utf8_find_white_space(s->data, s->length, pos, &start, &stop))
                        found = (int64_t)start;
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

        static int32_t
        tcs_string_rune_count(TcsString *s)
        {
            tcs_nonnull(s);
            return tcs_utf8_rune_count(s->data, s->length);
        }

        static TcsString *
        tcs_string_rune_substring(TcsString *s, int32_t start, int32_t count)
        {
            size_t from, to;
            tcs_nonnull(s);
            if (!tcs_utf8_rune_range(s->data, s->length, start, count, &from, &to))
                tcs_fault("RuneSubstring: range is out of the string");
            return tcs_string_new(s->data + from, to - from);
        }

        /* TinySystem.Text.Utf8Bytes: 文字列 text の [start, start + length) を指す値。既定値は text が NULL で長さ 0 */
        static int32_t
        tcs_utf8_bytes_text_length(TcsString *s)
        {
            tcs_nonnull(s);
            if (s->length > INT32_MAX) tcs_fault("string-length-overflow");
            return (int32_t)s->length;
        }

        static int32_t
        tcs_utf8_bytes_at(TcsString *text, int32_t start, int32_t length, int32_t index)
        {
            if (index < 0 || index >= length) tcs_fault("utf8-bytes-index");
            return text->data[start + index];
        }

        static void
        tcs_utf8_bytes_check_slice(int32_t length, int32_t start, int32_t count)
        {
            if (start < 0 || count < 0 || start > length || count > length - start)
                tcs_fault("Utf8Bytes.Slice: range is out of the bytes");
        }

        static TcsString *
        tcs_utf8_bytes_to_string(TcsString *text, int32_t start, int32_t length)
        {
            const unsigned char *data;
            TcsString *result;
            if (length == 0) return tcs_string_new(NULL, 0);
            data = text->data + start;
            result = tcs_string_new(NULL, tcs_utf8_scrub_length(data, (size_t)length));
            tcs_utf8_scrub(data, (size_t)length, result->data);
            return result;
        }

        static size_t
        tcs_string_length_add(size_t total, size_t length)
        {
            if (length > SIZE_MAX - total) tcs_fault("string-length-overflow");
            return total + length;
        }

        static TcsString *
        tcs_string_join_items(TcsString *sep, TcsString **items, size_t count)
        {
            size_t total = 0;
            size_t i;
            size_t at = 0;
            TcsString *result;
            tcs_nonnull(sep);
            /* null の要素は空文字列 (.NET と同じ) */
            for (i = 0; i < count; i++) {
                if (items[i] != NULL) total = tcs_string_length_add(total, items[i]->length);
                if (i > 0) total = tcs_string_length_add(total, sep->length);
            }
            result = tcs_string_new(NULL, total);
            for (i = 0; i < count; i++) {
                if (i > 0) { memcpy(result->data + at, sep->data, sep->length); at += sep->length; }
                if (items[i] == NULL) continue;
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

        /* 文字列化した要素を GC の外に並べる */
        static TcsString **
        tcs_string_join_buffer(size_t count)
        {
            TcsString **items;
            if (count > SIZE_MAX / sizeof(*items)) tcs_fault("allocation-overflow");
            items = malloc((count == 0 ? 1 : count) * sizeof(*items));
            if (items == NULL) tcs_fault("allocation");
            return items;
        }

        /* 値型の要素の string.Join: 要素を連結と同じく文字列化してから繋ぐ */
        enum { TCS_JOIN_I32, TCS_JOIN_F32, TCS_JOIN_BOOL };

        static TcsString *
        tcs_string_join_values(TcsString *sep, const void *data, size_t count, int kind)
        {
            TcsString **items = tcs_string_join_buffer(count);
            TcsString *result;
            size_t i;
            for (i = 0; i < count; i++)
                items[i] = kind == TCS_JOIN_I32 ? tcs_string_i32(((const int32_t *)data)[i])
                    : kind == TCS_JOIN_F32 ? tcs_string_f32(((const float *)data)[i])
                    : tcs_string_bool(((const bool *)data)[i]);
            result = tcs_string_join_items(sep, items, count);
            free(items);
            return result;
        }

        static TcsString *
        tcs_string_join_list_values(TcsString *sep, TcsList *list, int kind)
        {
            tcs_nonnull(list);
            return tcs_string_join_values(sep, list->data, list->length, kind);
        }

        static TcsString *
        tcs_string_join_array_values(TcsString *sep, TcsArray *array, int kind)
        {
            tcs_nonnull(array);
            return tcs_string_join_values(sep, array->data, array->length, kind);
        }

        /* enum の文字列化 (Enum.ToString)。names / values は .NET と同じ並び
           (Flags は全 member を値の符号なし昇順、それ以外は値ごとに 1 つ)。
           Flags は上位の member から含まれる bit を取り、名前を昇順に ", " で
           繋ぐ (取る member は 1 bit 以上を消すので 32 個まで)。どちらでも
           表せない値は数値 */
        static TcsString *
        tcs_enum_format(int32_t value, TcsString *const *names, const int32_t *values,
            int32_t count, bool flags)
        {
            int32_t found[32];
            int32_t found_count = 0;
            uint32_t rest = (uint32_t)value;
            size_t total = 0;
            size_t at = 0;
            int32_t i;
            TcsString *result;
            if (!flags) {
                for (i = 0; i < count; i++)
                    if (values[i] == value) return names[i];
                return tcs_string_i32(value);
            }
            if (value == 0)
                return count > 0 && values[0] == 0 ? names[0] : tcs_string_i32(0);
            for (i = count - 1; i >= 0 && rest != 0; i--) {
                uint32_t cur = (uint32_t)values[i];
                if (cur != 0 && (rest & cur) == cur) {
                    rest &= ~cur;
                    found[found_count++] = i;
                    total = tcs_string_length_add(total, names[i]->length);
                    if (found_count > 1) total = tcs_string_length_add(total, 2);
                }
            }
            if (rest != 0) return tcs_string_i32(value);
            result = tcs_string_new(NULL, total);
            for (i = found_count - 1; i >= 0; i--) {
                TcsString *name = names[found[i]];
                memcpy(result->data + at, name->data, name->length);
                at += name->length;
                if (i > 0) { memcpy(result->data + at, ", ", 2); at += 2; }
            }
            return result;
        }

        /* enum の要素の Join (要素ごとに tcs_enum_format) */
        static TcsString *
        tcs_string_join_enum(TcsString *sep, const int32_t *data, size_t count,
            TcsString *const *names, const int32_t *values, int32_t names_count, bool flags)
        {
            TcsString **items = tcs_string_join_buffer(count);
            TcsString *result;
            size_t i;
            for (i = 0; i < count; i++)
                items[i] = tcs_enum_format(data[i], names, values, names_count, flags);
            result = tcs_string_join_items(sep, items, count);
            free(items);
            return result;
        }

        /* tonumber: 10 進 (前後空白可)。失敗は Lua の nil → 後続で fault */
        static bool
        tcs_parse_i32(TcsString *s, int32_t *out)
        {
            /* NumberStyles.Integer: 前後の空白、先頭の符号、1 桁以上の 10 進数字 */
            const unsigned char *p;
            size_t i = 0;
            size_t digits = 0;
            long long value = 0;
            bool negative = false;
            tcs_nonnull(s);
            p = s->data;
            while (i < s->length && tcs_is_space(p[i])) i++;
            if (i < s->length && (p[i] == '+' || p[i] == '-')) negative = p[i++] == '-';
            for (; i < s->length && p[i] >= '0' && p[i] <= '9'; i++, digits++) {
                value = value * 10 + (p[i] - '0');
                if (value > 2147483648LL) return false;
            }
            while (i < s->length && tcs_is_space(p[i])) i++;
            if (digits == 0 || i != s->length) return false;
            if (negative) value = -value;
            if (value > INT32_MAX) return false;
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

        /* EnumerateRunes: 位置 *pos (byte) から 1 文字読む。壊れた並びは U+FFFD (runtime/utf8) */
        static int32_t
        tcs_utf8_next(TcsString *s, size_t *pos)
        {
            uint32_t rune;
            *pos += tcs_utf8_decode(s->data + *pos, s->length - *pos, &rune);
            return (int32_t)rune;
        }

        """;
}

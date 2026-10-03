namespace TinyCs.Tcs2c;

// C runtime の collection 節: 固定長配列 (inline 要素)、growable List
// (RAW buffer)、chained hash Dictionary (bucket 倍化)。要素 layout は
// 生成側が確保時に渡し、GC がそれで要素内 pointer を trace する。
internal sealed partial class CEmitter
{
    private const string RuntimeCollections = """
        /* ---- array / List / Dictionary ---- */
        static TcsArray *
        tcs_array_new(int32_t length, size_t element_size,
            const TcsLayout *layout)
        {
            TcsArray *array;
            if (length < 0) tcs_fault("negative-array-length");
            if ((size_t)length > (SIZE_MAX - sizeof(*array)) / element_size)
                tcs_fault("allocation-overflow");
            array = tcs_gc_alloc(TCS_KIND_ARRAY, layout,
                sizeof(*array) + (size_t)length * element_size);
            array->length = (size_t)length;
            array->element_size = element_size;
            return array;
        }

        static void *
        tcs_array_at(TcsArray *array, int32_t index)
        {
            tcs_nonnull(array);
            if (index < 0 || (size_t)index >= array->length)
                tcs_fault("bounds");
            return array->data + (size_t)index * array->element_size;
        }

        static int32_t
        tcs_array_length(TcsArray *array)
        {
            tcs_nonnull(array);
            if (array->length > INT32_MAX) tcs_fault("array-length-overflow");
            return (int32_t)array->length;
        }

        static TcsList *
        tcs_list_new(size_t element_size, const TcsLayout *layout)
        {
            TcsList *list = tcs_gc_alloc(TCS_KIND_LIST, layout, sizeof(*list));
            list->element_size = element_size;
            return list;
        }

        static void
        tcs_list_reserve(TcsList *list, size_t capacity)
        {
            void *grown;
            if (capacity <= list->capacity) return;
            if (capacity > SIZE_MAX / list->element_size)
                tcs_fault("allocation-overflow");
            grown = tcs_alloc(capacity * list->element_size);
            if (list->length != 0)
                memcpy(grown, list->data, list->length * list->element_size);
            list->data = grown;
            list->capacity = capacity;
            tcs_wb(list);
        }

        static void
        tcs_list_add(TcsList *list, const void *value, size_t element_size,
            const TcsLayout *layout)
        {
            tcs_nonnull(list);
            if (list->element_size == 0) {
                list->element_size = element_size;
                TCS_GC_HEADER(list)->layout = layout;
            }
            if (list->element_size != element_size) tcs_fault("list-element-type");
            if (list->length >= INT32_MAX) tcs_fault("list-length-overflow");
            if (list->length == list->capacity)
                tcs_list_reserve(list, list->capacity == 0 ? 4 : list->capacity * 2);
            memcpy((unsigned char *)list->data
                + list->length * list->element_size, value, list->element_size);
            list->length++;
            if (layout != NULL) tcs_wb(list);
        }

        static void *
        tcs_list_at(TcsList *list, int32_t index)
        {
            tcs_nonnull(list);
            if (index < 0 || (size_t)index >= list->length)
                tcs_fault("bounds");
            return (unsigned char *)list->data + (size_t)index * list->element_size;
        }

        static int32_t
        tcs_list_length(TcsList *list)
        {
            tcs_nonnull(list);
            return (int32_t)list->length;
        }

        static void
        tcs_list_clear(TcsList *list)
        {
            tcs_nonnull(list);
            if (list->length != 0)
                memset(list->data, 0, list->length * list->element_size);
            list->length = 0;
        }

        static void
        tcs_list_remove_at(TcsList *list, int32_t index)
        {
            unsigned char *base;
            tcs_nonnull(list);
            if (index < 0 || (size_t)index >= list->length) tcs_fault("bounds");
            base = (unsigned char *)list->data + (size_t)index * list->element_size;
            memmove(base, base + list->element_size,
                (list->length - (size_t)index - 1) * list->element_size);
            list->length--;
            memset((unsigned char *)list->data + list->length * list->element_size,
                0, list->element_size);
        }

        static TcsArray *
        tcs_list_to_array(TcsList *list)
        {
            TcsArray *array;
            tcs_nonnull(list);
            if (list->length > INT32_MAX) tcs_fault("list-length-overflow");
            array = tcs_array_new((int32_t)list->length, list->element_size,
                TCS_GC_HEADER(list)->layout);
            if (list->length != 0)
                memcpy(array->data, list->data, list->length * list->element_size);
            return array;
        }

        static TcsDict *
        tcs_dict_new(int32_t key_is_string, size_t value_size,
            const TcsLayout *layout)
        {
            TcsDict *dict = tcs_gc_alloc(TCS_KIND_DICT, layout, sizeof(*dict));
            dict->key_is_string = key_is_string;
            dict->value_size = value_size;
            return dict;
        }

        /* 外部 data class の先頭 (type_id の直後が host handle) */
        typedef struct TcsHandleObject {
            uint32_t type_id;
            uint64_t host_value;
        } TcsHandleObject;
        #define TCS_HANDLE_VALUE(ptr) (((const TcsHandleObject *)(const void *)(ptr))->host_value)

        static uint32_t
        tcs_dict_hash(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            uint32_t h = 2166136261u;
            if (dict->key_is_string == 2) {
                uint64_t value = TCS_HANDLE_VALUE(key_s);
                int i;
                for (i = 0; i < 8; i++)
                    h = (h ^ (uint32_t)((value >> (8 * i)) & 0xFF)) * 16777619u;
            } else if (dict->key_is_string) {
                size_t i;
                for (i = 0; i < key_s->length; i++)
                    h = (h ^ (unsigned char)key_s->data[i]) * 16777619u;
            } else {
                int i;
                for (i = 0; i < 4; i++)
                    h = (h ^ (((uint32_t)key_i >> (8 * i)) & 0xFF)) * 16777619u;
            }
            return h;
        }

        static bool
        tcs_dict_key_equal(TcsDict *dict, TcsDictNode *node, int32_t key_i,
            TcsString *key_s)
        {
            if (dict->key_is_string == 2)
                return node->key_s == key_s
                    || (TCS_HANDLE_VALUE(key_s) != 0
                        && TCS_HANDLE_VALUE(node->key_s) == TCS_HANDLE_VALUE(key_s));
            return dict->key_is_string
                ? tcs_string_equal(node->key_s, key_s)
                : node->key_i == key_i;
        }

        static TcsDictNode *
        tcs_dict_find(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            TcsDictNode *node;
            tcs_nonnull(dict);
            if (dict->key_is_string) tcs_nonnull(key_s);
            if (dict->bucket_count == 0) return NULL;
            node = dict->buckets[tcs_dict_hash(dict, key_i, key_s)
                % dict->bucket_count];
            for (; node != NULL; node = node->next)
                if (tcs_dict_key_equal(dict, node, key_i, key_s)) return node;
            return NULL;
        }

        static void
        tcs_dict_grow(TcsDict *dict)
        {
            size_t count = dict->bucket_count == 0 ? 16 : dict->bucket_count * 2;
            TcsDictNode **buckets;
            size_t i;
            if (count > SIZE_MAX / sizeof(*buckets)) tcs_fault("allocation-overflow");
            buckets = tcs_alloc(count * sizeof(*buckets));
            for (i = 0; i < dict->bucket_count; i++) {
                TcsDictNode *node = dict->buckets[i];
                while (node != NULL) {
                    TcsDictNode *next = node->next;
                    size_t bucket = tcs_dict_hash(dict, node->key_i, node->key_s)
                        % count;
                    node->next = buckets[bucket];
                    /* 旧 node の next が若い node を指しうる */
                    tcs_wb(node);
                    buckets[bucket] = node;
                    node = next;
                }
            }
            dict->buckets = buckets;
            dict->bucket_count = count;
            tcs_wb(dict);
        }

        static void *
        tcs_dict_put(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            TcsDictNode *node = tcs_dict_find(dict, key_i, key_s);
            size_t bucket;
            /* 返した value slot へ呼び出し側が書く: 既存 node は owner として登録 */
            if (node != NULL) { tcs_wb(node); return node->value; }
            if ((size_t)dict->count + 1 > dict->bucket_count * 2) tcs_dict_grow(dict);
            node = tcs_gc_alloc(TCS_KIND_DICT_NODE, TCS_GC_HEADER(dict)->layout,
                sizeof(*node) + dict->value_size);
            node->key_i = key_i;
            node->key_s = key_s;
            bucket = tcs_dict_hash(dict, key_i, key_s) % dict->bucket_count;
            node->next = dict->buckets[bucket];
            dict->buckets[bucket] = node;
            dict->count++;
            tcs_wb(dict);
            return node->value;
        }

        static void *
        tcs_dict_at(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            TcsDictNode *node = tcs_dict_find(dict, key_i, key_s);
            if (node == NULL) tcs_fault("key-not-found");
            return node->value;
        }

        static bool
        tcs_dict_contains(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            return tcs_dict_find(dict, key_i, key_s) != NULL;
        }

        static bool
        tcs_dict_tryget(TcsDict *dict, int32_t key_i, TcsString *key_s,
            void *out_value, const void *fallback)
        {
            TcsDictNode *node = tcs_dict_find(dict, key_i, key_s);
            memcpy(out_value, node != NULL ? node->value : fallback,
                dict->value_size);
            return node != NULL;
        }

        static bool
        tcs_dict_remove(TcsDict *dict, int32_t key_i, TcsString *key_s)
        {
            TcsDictNode **link;
            tcs_nonnull(dict);
            if (dict->key_is_string) tcs_nonnull(key_s);
            if (dict->bucket_count == 0) return false;
            link = &dict->buckets[tcs_dict_hash(dict, key_i, key_s)
                % dict->bucket_count];
            for (; *link != NULL; link = &(*link)->next) {
                if (tcs_dict_key_equal(dict, *link, key_i, key_s)) {
                    *link = (*link)->next;
                    dict->count--;
                    return true;
                }
            }
            return false;
        }

        static int32_t
        tcs_dict_count(TcsDict *dict)
        {
            tcs_nonnull(dict);
            return dict->count;
        }

        """;
}

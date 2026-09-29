namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private const string GcRuntime = """
        typedef union TcsGcBlock TcsGcBlock;
        union TcsGcBlock {
            max_align_t alignment;
            struct {
                TcsGcBlock *next, *previous, *gray;
                TcsTrace trace;
                size_t size;
                bool marked;
            } h;
        };
        static TcsGcBlock *tcs_gc_blocks, *tcs_gc_gray;
        static size_t tcs_gc_bytes, tcs_gc_objects;
        static size_t tcs_gc_threshold = 128 * 1024;
        static unsigned tcs_gc_call_depth;

        static void *
        tcs_alloc_traced(size_t size, TcsTrace trace)
        {
            if (size > SIZE_MAX - sizeof(TcsGcBlock)
                || size + sizeof(TcsGcBlock) > SIZE_MAX - tcs_gc_bytes)
                tcs_fault("allocation-overflow");
            TcsGcBlock *block = calloc(1, sizeof(*block) + size);
            if (block == NULL) tcs_fault("allocation");
            block->h.size = size;
            block->h.trace = trace;
            block->h.next = tcs_gc_blocks;
            if (tcs_gc_blocks != NULL) tcs_gc_blocks->h.previous = block;
            tcs_gc_blocks = block;
            tcs_gc_bytes += sizeof(*block) + size;
            tcs_gc_objects++;
            return block + 1;
        }

        static void *
        tcs_alloc(size_t size)
        {
            return tcs_alloc_traced(size, NULL);
        }

        static void *
        tcs_realloc(void *old, size_t size)
        {
            if (old == NULL) return tcs_alloc(size);
            TcsGcBlock *block = (TcsGcBlock *)old - 1;
            size_t old_size = block->h.size;
            if (size > SIZE_MAX - sizeof(*block)
                || size > SIZE_MAX - (tcs_gc_bytes - old_size))
                tcs_fault("allocation-overflow");
            block = realloc(block, sizeof(*block) + size);
            if (block == NULL) tcs_fault("allocation");
            if (block->h.previous != NULL) block->h.previous->h.next = block;
            else tcs_gc_blocks = block;
            if (block->h.next != NULL) block->h.next->h.previous = block;
            block->h.size = size;
            tcs_gc_bytes = tcs_gc_bytes - old_size + size;
            return block + 1;
        }

        static void
        tcs_gc_mark(void *value)
        {
            if (value == NULL) return;
            TcsGcBlock *block = (TcsGcBlock *)value - 1;
            if (block->h.marked) return;
            block->h.marked = true;
            block->h.gray = tcs_gc_gray;
            tcs_gc_gray = block;
        }

        static void
        tcs_trace_ref(void *slot)
        {
            void *value;
            memcpy(&value, slot, sizeof(value));
            tcs_gc_mark(value);
        }

        static void
        tcs_trace_array(void *object)
        {
            TcsArray *array = object;
            tcs_gc_mark(array->data);
            if (array->trace_element != NULL)
                for (size_t i = 0; i < array->length; i++)
                    array->trace_element((unsigned char *)array->data
                        + i * array->element_size);
        }

        static void
        tcs_trace_list(void *object)
        {
            TcsList *list = object;
            tcs_gc_mark(list->data);
            if (list->trace_element != NULL)
                for (size_t i = 0; i < list->length; i++)
                    list->trace_element((unsigned char *)list->data
                        + i * list->element_size);
        }

        static void
        tcs_trace_dict(void *object)
        {
            TcsDict *dict = object;
            for (size_t i = 0; i < TCS_DICT_BUCKETS; i++)
                for (TcsDictNode *node = dict->buckets[i]; node; node = node->next) {
                    tcs_gc_mark(node);
                    if (dict->key_is_string) tcs_gc_mark(node->key_s);
                    if (dict->trace_value != NULL) dict->trace_value(node->value);
                }
        }

        static void
        tcs_trace_closure(void *object)
        {
            TcsClosure *closure = object;
            for (size_t i = 0; i < closure->count; i++)
                tcs_gc_mark(closure->cells[i]);
        }

        static void
        tcs_gc_collect(TcsTrace roots)
        {
            if (tcs_gc_call_depth != 0) tcs_fault("collection-during-call");
            roots(NULL);
            while (tcs_gc_gray != NULL) {
                TcsGcBlock *block = tcs_gc_gray;
                tcs_gc_gray = block->h.gray;
                if (block->h.trace != NULL) block->h.trace(block + 1);
            }
            TcsGcBlock *block = tcs_gc_blocks;
            while (block != NULL) {
                TcsGcBlock *next = block->h.next;
                if (block->h.marked) block->h.marked = false;
                else {
                    if (block->h.previous != NULL) block->h.previous->h.next = next;
                    else tcs_gc_blocks = next;
                    if (next != NULL) next->h.previous = block->h.previous;
                    tcs_gc_bytes -= sizeof(*block) + block->h.size;
                    tcs_gc_objects--;
                    free(block);
                }
                block = next;
            }
            tcs_gc_threshold = tcs_gc_bytes > (SIZE_MAX - 128 * 1024) / 2
                ? SIZE_MAX : tcs_gc_bytes * 2 + 128 * 1024;
        }

        """;
}

namespace TinyCs.Tcs2c;

// C runtime の GC 節: フレーム同期の世代別 GC (T252)。
//
// - nursery: bump 確保の chunk 列。フレーム中 (entry の呼び出し中) は一切
//   GC しない。足りなければ chunk を足すだけ。
// - フレーム境界 (tcs_gc_frame。host が entry から戻った後に呼ぶ = C# の
//   スタックが空): static field / dirty な旧 object / host の hold slot から
//   到達する若い object だけを旧世代へ copy (Cheney、forwarding は header の
//   next)、残りの nursery は pointer のリセットで一括解放する。root は
//   全部精密で、C stack の走査は無い。
// - 旧世代: malloc 個別 + 連結 list。昇格 bytes が閾値を超えたら同じ境界で
//   mark-sweep (root は static + hold。この時点で若い object は無い)。
// - ライトバリア: 旧 object の参照型 slot への store で owner を dirty 登録
//   (tcs_wb)。生成側は field / 要素 / cell / struct 連鎖の store に挿し、
//   runtime は List / Dict の内部 store に挿す。static は毎境界で全走査
//   するのでバリア不要。
// 実行形 (main) は Main 全体が 1 フレームで GC は走らない (lib 出荷形が GC
// の対象)。
internal sealed partial class CEmitter
{
    private const string RuntimeGc = """
        /* ---- GC: フレーム同期の世代別 ---- */
        #ifndef TCS_GC_MIN_THRESHOLD
        #define TCS_GC_MIN_THRESHOLD ((size_t)1 << 20)
        #endif
        #ifndef TCS_GC_NURSERY_CHUNK
        #ifdef TCS_GC_STRESS
        #define TCS_GC_NURSERY_CHUNK ((size_t)256)
        #else
        #define TCS_GC_NURSERY_CHUNK ((size_t)1 << 18)
        #endif
        #endif
        #define TCS_GC_ALIGN (alignof(max_align_t))

        static const uint32_t tcs_layout_ptr_offsets[1] = { 0 };
        static const TcsLayout tcs_layout_ptr =
            { sizeof(void *), 1, tcs_layout_ptr_offsets };

        typedef struct TcsChunk {
            struct TcsChunk *next;
            size_t capacity;
            size_t used;
            _Alignas(max_align_t) unsigned char data[];
        } TcsChunk;

        static TcsChunk *tcs_gc_nursery;        /* 先頭 chunk (常に 1 個は残す) */
        static TcsChunk *tcs_gc_nursery_cur;    /* 確保中の chunk */
        static size_t tcs_gc_nursery_bytes;     /* 今フレームの確保 bytes */
        static size_t tcs_gc_nursery_chunks;

        static TcsGcHeader *tcs_gc_objects;     /* 旧世代の連結 list */
        static size_t tcs_gc_object_count;
        static size_t tcs_gc_live_bytes;        /* 直近 mark-sweep 後の生存 bytes */
        static size_t tcs_gc_allocated_since;   /* 直近 mark-sweep 以降の昇格 bytes */
        static size_t tcs_gc_threshold = TCS_GC_MIN_THRESHOLD;
        static size_t tcs_gc_collections;       /* 旧世代 mark-sweep 回数 */
        static size_t tcs_gc_frames;            /* 境界回数 */
        static size_t tcs_gc_promoted_bytes;    /* 累計昇格 bytes */
        static size_t tcs_gc_old_bytes;         /* 旧世代の現在 bytes (header 込み) */
        static size_t tcs_gc_nursery_objects;   /* 今フレームの確保 object 数 */
        static unsigned tcs_gc_call_depth;      /* entry の入れ子深さ (0 = 境界) */
        static int tcs_gc_collecting;

        static TcsGcHeader **tcs_gc_mark_stack; /* mark / 昇格 scan の両方で使う */
        static size_t tcs_gc_mark_count;
        static size_t tcs_gc_mark_capacity;
        static TcsGcHeader **tcs_gc_dirty;      /* remembered set (旧 object) */
        static size_t tcs_gc_dirty_count;
        static size_t tcs_gc_dirty_capacity;
        static void ***tcs_gc_holds;            /* host が frame を跨いで持つ slot */
        static size_t tcs_gc_hold_count;
        static size_t tcs_gc_hold_capacity;

        static void
        tcs_gc_push(TcsGcHeader *hdr)
        {
            if (hdr->flags & (TCS_GC_MARK | TCS_GC_STATIC)) return;
            hdr->flags |= TCS_GC_MARK;
            if (tcs_gc_mark_count == tcs_gc_mark_capacity) {
                size_t capacity = tcs_gc_mark_capacity == 0
                    ? 1024 : tcs_gc_mark_capacity * 2;
                TcsGcHeader **grown = realloc(tcs_gc_mark_stack,
                    capacity * sizeof(*grown));
                if (grown == NULL) tcs_fault("allocation");
                tcs_gc_mark_stack = grown;
                tcs_gc_mark_capacity = capacity;
            }
            tcs_gc_mark_stack[tcs_gc_mark_count++] = hdr;
        }

        /* ---- ライトバリア: 旧 object への store で owner を dirty 登録 ---- */
        static void
        tcs_wb(const void *owner)
        {
            TcsGcHeader *hdr;
            if (owner == NULL) return;
            hdr = TCS_GC_HEADER(owner);
            if ((hdr->flags & (TCS_GC_OLD | TCS_GC_DIRTY)) != TCS_GC_OLD) return;
            hdr->flags |= TCS_GC_DIRTY;
            if (tcs_gc_dirty_count == tcs_gc_dirty_capacity) {
                size_t capacity = tcs_gc_dirty_capacity == 0
                    ? 256 : tcs_gc_dirty_capacity * 2;
                TcsGcHeader **grown = realloc(tcs_gc_dirty, capacity * sizeof(*grown));
                if (grown == NULL) tcs_fault("allocation");
                tcs_gc_dirty = grown;
                tcs_gc_dirty_capacity = capacity;
            }
            tcs_gc_dirty[tcs_gc_dirty_count++] = hdr;
        }

        /* ---- 旧世代の mark (精密 pointer のみ) ---- */
        static void
        tcs_gc_mark_ptr(const void *pointer)
        {
            if (pointer != NULL) tcs_gc_push(TCS_GC_HEADER(pointer));
        }

        static void
        tcs_gc_mark_value(const void *value, const TcsLayout *layout)
        {
            uint32_t i;
            if (layout == NULL) return;
            for (i = 0; i < layout->count; i++) {
                void *pointer;
                memcpy(&pointer, (const unsigned char *)value + layout->offsets[i],
                    sizeof(pointer));
                tcs_gc_mark_ptr(pointer);
            }
        }

        static void
        tcs_gc_trace(TcsGcHeader *hdr)
        {
            void *payload = TCS_GC_PAYLOAD(hdr);
            size_t i;
            switch (hdr->kind) {
            case TCS_KIND_OBJECT:
                tcs_gc_mark_value(payload, hdr->layout);
                break;
            case TCS_KIND_ARRAY: {
                TcsArray *array = payload;
                if (hdr->layout == NULL) break;
                for (i = 0; i < array->length; i++)
                    tcs_gc_mark_value(array->data + i * array->element_size,
                        hdr->layout);
                break;
            }
            case TCS_KIND_LIST: {
                TcsList *list = payload;
                tcs_gc_mark_ptr(list->data);
                if (hdr->layout == NULL || list->data == NULL) break;
                for (i = 0; i < list->length; i++)
                    tcs_gc_mark_value((unsigned char *)list->data
                        + i * list->element_size, hdr->layout);
                break;
            }
            case TCS_KIND_DICT: {
                TcsDict *dict = payload;
                tcs_gc_mark_ptr(dict->buckets);
                for (i = 0; i < dict->bucket_count; i++)
                    tcs_gc_mark_ptr(dict->buckets[i]);
                break;
            }
            case TCS_KIND_DICT_NODE: {
                TcsDictNode *node = payload;
                tcs_gc_mark_ptr(node->next);
                tcs_gc_mark_ptr(node->key_s);
                tcs_gc_mark_value(node->value, hdr->layout);
                break;
            }
            case TCS_KIND_CLOSURE: {
                TcsClosure *closure = payload;
                size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);
                for (i = 0; i < cells; i++) tcs_gc_mark_ptr(closure->cells[i]);
                break;
            }
            default:
                break;
            }
        }

        /* 旧世代の mark-sweep。root は static と host hold (境界なので若い
           object は存在せず、C stack 上の参照も無い) */
        static void __attribute__((noinline))
        tcs_gc_collect(void)
        {
            TcsGcHeader *hdr;
            TcsGcHeader **link;
            size_t i;
            size_t live = 0;
            size_t survivors = 0;
            if (tcs_gc_collecting) return;
            tcs_gc_collecting = 1;
            tcs_gc_mark_count = 0;
            tcs_gc_mark_statics();
            for (i = 0; i < tcs_gc_hold_count; i++)
                tcs_gc_mark_ptr(*tcs_gc_holds[i]);
            while (tcs_gc_mark_count > 0)
                tcs_gc_trace(tcs_gc_mark_stack[--tcs_gc_mark_count]);

            link = &tcs_gc_objects;
            while (*link != NULL) {
                hdr = *link;
                if (hdr->flags & TCS_GC_MARK) {
                    hdr->flags &= ~(uint32_t)TCS_GC_MARK;
                    live += sizeof(*hdr) + hdr->size;
                    survivors++;
                    link = &hdr->next;
                } else {
                    *link = hdr->next;
                    free(hdr);
                }
            }
            tcs_gc_object_count = survivors;
            tcs_gc_live_bytes = live;
            tcs_gc_old_bytes = live;
            tcs_gc_allocated_since = 0;
            tcs_gc_threshold = live > TCS_GC_MIN_THRESHOLD
                ? live : TCS_GC_MIN_THRESHOLD;
            tcs_gc_collections++;
            tcs_gc_collecting = 0;
        }

        /* ---- nursery (bump 確保) ---- */
        static TcsChunk *
        tcs_gc_chunk_new(size_t capacity)
        {
            TcsChunk *chunk;
            if (capacity < TCS_GC_NURSERY_CHUNK) capacity = TCS_GC_NURSERY_CHUNK;
            chunk = calloc(1, sizeof(*chunk) + capacity);
            if (chunk == NULL) tcs_fault("allocation");
            chunk->capacity = capacity;
            tcs_gc_nursery_chunks++;
            return chunk;
        }

        static void *
        tcs_gc_alloc(uint32_t kind, const TcsLayout *layout, size_t size)
        {
            TcsGcHeader *hdr;
            size_t need;
            if (size > SIZE_MAX - sizeof(*hdr) - TCS_GC_ALIGN)
                tcs_fault("allocation-overflow");
            need = (sizeof(*hdr) + size + (TCS_GC_ALIGN - 1)) & ~(TCS_GC_ALIGN - 1);
            if (tcs_gc_nursery_cur == NULL) {
                tcs_gc_nursery = tcs_gc_nursery_cur = tcs_gc_chunk_new(need);
            } else if (tcs_gc_nursery_cur->used + need > tcs_gc_nursery_cur->capacity) {
                TcsChunk *chunk = tcs_gc_chunk_new(need);
                tcs_gc_nursery_cur->next = chunk;
                tcs_gc_nursery_cur = chunk;
            }
            hdr = (TcsGcHeader *)(tcs_gc_nursery_cur->data + tcs_gc_nursery_cur->used);
            tcs_gc_nursery_cur->used += need;
            tcs_gc_nursery_bytes += need;
            tcs_gc_nursery_objects++;
            hdr->next = NULL;
            hdr->layout = layout;
            hdr->size = size;
            hdr->kind = (uint16_t)kind;
            hdr->flags = 0;
            hdr->type_id = 0;
            return TCS_GC_PAYLOAD(hdr);
        }

        /* 境界で nursery を空にする: 先頭 chunk だけ残して zero に戻す */
        static void
        tcs_gc_nursery_reset(void)
        {
            TcsChunk *chunk;
            if (tcs_gc_nursery == NULL) return;
            chunk = tcs_gc_nursery->next;
            while (chunk != NULL) {
                TcsChunk *next = chunk->next;
                free(chunk);
                chunk = next;
            }
            tcs_gc_nursery->next = NULL;
            memset(tcs_gc_nursery->data, 0, tcs_gc_nursery->used);
            tcs_gc_nursery->used = 0;
            tcs_gc_nursery_cur = tcs_gc_nursery;
            tcs_gc_nursery_chunks = 1;
            tcs_gc_nursery_bytes = 0;
            tcs_gc_nursery_objects = 0;
        }

        /* ---- 昇格 (若い object を旧世代へ copy、Cheney) ---- */
        static void *
        tcs_gc_promote(void *pointer)
        {
            TcsGcHeader *hdr;
            TcsGcHeader *copy;
            if (pointer == NULL) return NULL;
            hdr = TCS_GC_HEADER(pointer);
            if (hdr->flags & (TCS_GC_OLD | TCS_GC_STATIC)) return pointer;
            if (hdr->flags & TCS_GC_FORWARD) return TCS_GC_PAYLOAD(hdr->next);
            copy = malloc(sizeof(*copy) + hdr->size);
            if (copy == NULL) tcs_fault("allocation");
            memcpy(copy, hdr, sizeof(*copy) + hdr->size);
            copy->flags = TCS_GC_OLD;
            copy->next = tcs_gc_objects;
            tcs_gc_objects = copy;
            tcs_gc_object_count++;
            tcs_gc_allocated_since += sizeof(*copy) + hdr->size;
            tcs_gc_promoted_bytes += sizeof(*copy) + hdr->size;
            tcs_gc_old_bytes += sizeof(*copy) + hdr->size;
            hdr->next = copy;
            hdr->flags |= TCS_GC_FORWARD;
            /* 中身の slot は後で tcs_gc_forward_object (scan queue) */
            if (tcs_gc_mark_count == tcs_gc_mark_capacity) {
                size_t capacity = tcs_gc_mark_capacity == 0
                    ? 1024 : tcs_gc_mark_capacity * 2;
                TcsGcHeader **grown = realloc(tcs_gc_mark_stack,
                    capacity * sizeof(*grown));
                if (grown == NULL) tcs_fault("allocation");
                tcs_gc_mark_stack = grown;
                tcs_gc_mark_capacity = capacity;
            }
            tcs_gc_mark_stack[tcs_gc_mark_count++] = copy;
            return TCS_GC_PAYLOAD(copy);
        }

        static void
        tcs_gc_forward_slot(void *slot)
        {
            void *pointer;
            memcpy(&pointer, slot, sizeof(pointer));
            pointer = tcs_gc_promote(pointer);
            memcpy(slot, &pointer, sizeof(pointer));
        }

        static void
        tcs_gc_forward_value(void *value, const TcsLayout *layout)
        {
            uint32_t i;
            if (layout == NULL) return;
            for (i = 0; i < layout->count; i++)
                tcs_gc_forward_slot((unsigned char *)value + layout->offsets[i]);
        }

        /* 旧 object (昇格直後 / dirty) の全 pointer slot を昇格先へ書き換える */
        static void
        tcs_gc_forward_object(TcsGcHeader *hdr)
        {
            void *payload = TCS_GC_PAYLOAD(hdr);
            size_t i;
            switch (hdr->kind) {
            case TCS_KIND_OBJECT:
                tcs_gc_forward_value(payload, hdr->layout);
                break;
            case TCS_KIND_ARRAY: {
                TcsArray *array = payload;
                if (hdr->layout == NULL) break;
                for (i = 0; i < array->length; i++)
                    tcs_gc_forward_value(array->data + i * array->element_size,
                        hdr->layout);
                break;
            }
            case TCS_KIND_LIST: {
                TcsList *list = payload;
                list->data = tcs_gc_promote(list->data);
                if (hdr->layout == NULL || list->data == NULL) break;
                for (i = 0; i < list->length; i++)
                    tcs_gc_forward_value((unsigned char *)list->data
                        + i * list->element_size, hdr->layout);
                break;
            }
            case TCS_KIND_DICT: {
                TcsDict *dict = payload;
                dict->buckets = tcs_gc_promote(dict->buckets);
                for (i = 0; i < dict->bucket_count; i++)
                    tcs_gc_forward_slot(&dict->buckets[i]);
                break;
            }
            case TCS_KIND_DICT_NODE: {
                TcsDictNode *node = payload;
                tcs_gc_forward_slot(&node->next);
                tcs_gc_forward_slot(&node->key_s);
                tcs_gc_forward_value(node->value, hdr->layout);
                break;
            }
            case TCS_KIND_CLOSURE: {
                TcsClosure *closure = payload;
                size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);
                for (i = 0; i < cells; i++) tcs_gc_forward_slot(&closure->cells[i]);
                break;
            }
            default:
                break;
            }
        }

        /* フレーム境界: C# のスタックが空のときに host が呼ぶ */
        static void __attribute__((noinline))
        tcs_gc_frame(void)
        {
            size_t i;
            tcs_gc_mark_count = 0;
            tcs_gc_forward_statics();
            for (i = 0; i < tcs_gc_hold_count; i++)
                tcs_gc_forward_slot(tcs_gc_holds[i]);
            for (i = 0; i < tcs_gc_dirty_count; i++) {
                tcs_gc_dirty[i]->flags &= ~(uint32_t)TCS_GC_DIRTY;
                tcs_gc_forward_object(tcs_gc_dirty[i]);
            }
            tcs_gc_dirty_count = 0;
            while (tcs_gc_mark_count > 0)
                tcs_gc_forward_object(tcs_gc_mark_stack[--tcs_gc_mark_count]);
            tcs_gc_nursery_reset();
            tcs_gc_frames++;
        #ifdef TCS_GC_STRESS
            tcs_gc_collect();
        #else
            if (tcs_gc_allocated_since >= tcs_gc_threshold) tcs_gc_collect();
        #endif
        }

        /* host が frame を跨いで持つ pointer の slot (境界で昇格先へ書き換わる) */
        static void
        tcs_gc_hold(void **slot)
        {
            if (tcs_gc_hold_count == tcs_gc_hold_capacity) {
                size_t capacity = tcs_gc_hold_capacity == 0
                    ? 16 : tcs_gc_hold_capacity * 2;
                void ***grown = realloc(tcs_gc_holds, capacity * sizeof(*grown));
                if (grown == NULL) tcs_fault("allocation");
                tcs_gc_holds = grown;
                tcs_gc_hold_capacity = capacity;
            }
            tcs_gc_holds[tcs_gc_hold_count++] = slot;
        }

        static void
        tcs_gc_release(void **slot)
        {
            size_t i;
            for (i = 0; i < tcs_gc_hold_count; i++) {
                if (tcs_gc_holds[i] != slot) continue;
                tcs_gc_holds[i] = tcs_gc_holds[--tcs_gc_hold_count];
                return;
            }
        }

        static void *
        tcs_alloc(size_t size)
        {
            return tcs_gc_alloc(TCS_KIND_RAW, NULL, size);
        }

        static void *
        tcs_new_object(const TcsLayout *layout)
        {
            return tcs_gc_alloc(TCS_KIND_OBJECT, layout, layout->size);
        }

        static void *
        tcs_new_cell(size_t size, const TcsLayout *layout)
        {
            return tcs_gc_alloc(TCS_KIND_OBJECT, layout, size);
        }

        static TcsClosure *
        tcs_new_closure(void *fn, size_t cells)
        {
            TcsClosure *closure = tcs_gc_alloc(TCS_KIND_CLOSURE, NULL,
                sizeof(*closure) + cells * sizeof(void *));
            closure->fn = fn;
            return closure;
        }

        """;
}

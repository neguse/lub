#include "foreign.c"

/* host 側の foreign 実装 (生成 C を include して内部 ABI を直接使う):
   外部 data class は tcs_new_<Class>() で作り host_value に handle を置く。
   nullable スカラは TcsOpt* の値渡し、配列の要素型は GC header の type tag。
   entry 中に作った object は static に置けば境界で昇格して生き残る */
Tcs_Resource *tcs_host_api_current(void)
{
    Tcs_Resource *value = tcs_new_Resource();
    value->host_value = 99;
    value->f_version = 4;
    return value;
}

/* stub の instance method は host 関数 tcs_host_<Class>_<method>(self, args...) */
int32_t tcs_host_resource_get(Tcs_Resource *self, int32_t index)
{
    return self->host_value == 99 ? 100 + index : -1;
}

int32_t tcs_host_resource_peek(Tcs_Resource *self, int32_t i)
{
    return 100 + i;
}

void tcs_host_resource_touch(Tcs_Resource *self)
{
    if (self->host_value != 99) tcs_fault("foreign-touch");
}

int32_t tcs_host_baseoptions_scale(Tcs_BaseOptions *self, int32_t n)
{
    return self->f_version.v * n;
}

Tcs_Handle *tcs_host_api_body(int32_t n)
{
    Tcs_Handle *value = tcs_new_Handle();
    value->host_value = UINT64_C(0x100000000) + (uint64_t)n;
    return value;
}

float tcs_host_api_read(Tcs_Options *options, int32_t mode, TcsOptI32 version)
{
    if (mode != 3 || version.has || !options->f_version.has || options->f_version.v != 2)
        tcs_fault("foreign-options");
    if (TCS_GC_HEADER(options->f_data)->type_id != TCS_TYPE_ARRAY_F32) tcs_fault("foreign-array-type");
    return ((float *)options->f_data->data)[1];
}

void tcs_host_api_poll(TcsString **topic, TcsString **payload)
{
    *topic = tcs_string_new((const unsigned char *)"topic", 5);
    *payload = tcs_string_new((const unsigned char *)"payload", 7);
}

int main(void)
{
    tcs_lib_init();
    tcs_entry_Foreign_init();
    tcs_lib_collect();
    if (tcs_lib_heap_bytes() == 0) return 1;
    if (tcs_s_Foreign_resource->host_value != 99) return 1;
    tcs_entry_Foreign_frame(0.5f);
    tcs_lib_collect();
    return tcs_lib_heap_bytes() != 0;
}

void tcs_host_api_discard(int32_t *count, TcsString **text, float *dt)
{
    *count = 42;
    *text = tcs_string_new((const unsigned char *)"discard", 7);
    *dt = 0.5f;
    if (*count != 42 || (*text)->length != 7 || *dt != 0.5f) tcs_fault("aliased-discard");
}

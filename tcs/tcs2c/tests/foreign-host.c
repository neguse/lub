#include "foreign.c"

Tcs_Resource *tcs_host_api_current(void)
{
    Tcs_Resource *value = tcs_new_Resource(sizeof(*value), tcs_trace_object_Resource);
    value->host_value = 99;
    value->f_version = 4;
    return value;
}

float tcs_host_api_read(Tcs_Options *options, int32_t mode, void *version)
{
    if (mode != 3 || version != NULL || tcs_unbox(options->f_version, TCS_BOX_I32)->value.i != 2)
        tcs_fault("foreign-options");
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

#include <stddef.h>

void tcs_lib_init(void);
void tcs_lib_collect(void);
size_t tcs_lib_heap_bytes(void);
void tcs_entry_Generics_retain(void);
void tcs_entry_Generics_print_and_clear(void);

int main(void)
{
    tcs_lib_init();
    tcs_entry_Generics_retain();
    tcs_lib_collect();
    if (tcs_lib_heap_bytes() == 0) return 1;
    tcs_entry_Generics_print_and_clear();
    tcs_lib_collect();
    return tcs_lib_heap_bytes() != 0;
}

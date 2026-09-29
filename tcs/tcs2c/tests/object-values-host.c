#include <stddef.h>

void tcs_lib_init(void);
void tcs_lib_collect(void);
size_t tcs_lib_heap_bytes(void);
void tcs_entry_ObjectValues_init(void);
void tcs_entry_ObjectValues_check(void);
void tcs_entry_Interfaces_init(void);
void tcs_entry_Interfaces_check(void);

int main(void)
{
    tcs_lib_init();
    tcs_entry_ObjectValues_init();
    tcs_entry_Interfaces_init();
    tcs_lib_collect();
    if (tcs_lib_heap_bytes() == 0) return 1;
    tcs_entry_ObjectValues_check();
    tcs_entry_Interfaces_check();
    tcs_lib_collect();
    return tcs_lib_heap_bytes() != 0;
}

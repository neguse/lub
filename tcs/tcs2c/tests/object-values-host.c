#include <stddef.h>

void tcs_lib_init(void);
void tcs_lib_collect(void);
size_t tcs_lib_heap_bytes(void);
void tcs_entry_ObjectValues_Init(void);
void tcs_entry_ObjectValues_Check(void);
void tcs_entry_Interfaces_Init(void);
void tcs_entry_Interfaces_Check(void);

int main(void)
{
    tcs_lib_init();
    tcs_entry_ObjectValues_Init();
    tcs_entry_Interfaces_Init();
    tcs_lib_collect();
    if (tcs_lib_heap_bytes() == 0) return 1;
    tcs_entry_ObjectValues_Check();
    tcs_entry_Interfaces_Check();
    tcs_lib_collect();
    return tcs_lib_heap_bytes() != 0;
}

#include <assert.h>
#include <stddef.h>
#include <stdio.h>

void tcs_lib_init(void);
void tcs_lib_collect(void);
size_t tcs_lib_heap_bytes(void);
size_t tcs_lib_heap_objects(void);
void tcs_entry_GcProbe_setup(void);
void tcs_entry_GcProbe_churn(void);
void tcs_entry_GcProbe_check(void);
void tcs_entry_GcProbe_clear(void);

int main(void)
{
    tcs_lib_init();
    tcs_entry_GcProbe_setup();
    tcs_lib_collect();
    size_t bytes = tcs_lib_heap_bytes();
    size_t objects = tcs_lib_heap_objects();
    assert(bytes > 0 && objects > 0);
    for (int i = 0; i < 100; i++) {
        tcs_entry_GcProbe_churn();
        assert(tcs_lib_heap_bytes() < bytes * 2 + 1024 * 1024);
    }
    for (int i = 0; i < 100; i++) {
        tcs_entry_GcProbe_churn();
        tcs_lib_collect();
        assert(tcs_lib_heap_bytes() == bytes);
        assert(tcs_lib_heap_objects() == objects);
    }
    tcs_entry_GcProbe_check();
    tcs_entry_GcProbe_clear();
    tcs_lib_collect();
    assert(tcs_lib_heap_bytes() == 0);
    assert(tcs_lib_heap_objects() == 0);
    puts("gc: roots survive, cycles reclaimed, heap bounded");
    return 0;
}

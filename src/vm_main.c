// 既製の tcs VM の player (lub_vm) の main。
// 中身は library (src/vm_player.c、include/lub/lub_vm.h)。
// native の拡張は静的に持たず、X.ref.cs の隣の X.so を読む。
#include <lub/lub_vm_main.h>

LUB_VM_MAIN(NULL)

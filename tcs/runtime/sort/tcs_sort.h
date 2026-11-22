/* Sorting for List.Sort. The same introsort as .NET's List<T>.Sort
   (ArraySortHelper<T> in dotnet/runtime), matching even the order of equal
   elements and the sequence of comparison calls (count, order, arguments)
   (il-spec §13). Used by tcsvm. tcs2c has the same steps in the generated C (to
   be shared per doc/vm-design.md §15.1). */
#ifndef TCS_SORT_H
#define TCS_SORT_H

#include <stddef.h>

/* Comparison. Negative if left < right, 0 if equal, positive if greater.
   If a reason to stop (fault) occurs inside the comparison, set *stop to
   nonzero. No further comparisons are called */
typedef int (*TcsSortCompare)(void *ctx, const void *left, const void *right,
                              int *stop);

typedef enum TcsSortStatus {
  TCS_SORT_OK = 0,
  TCS_SORT_STOPPED = 1,      /* the comparison set *stop */
  TCS_SORT_INCONSISTENT = 2, /* the comparison was inconsistent and the scan
                                went outside the range (.NET throws) */
} TcsSortStatus;

/* Sorts keys[0..n) (elements of es bytes). scratch is a work area of 2 * es
 * bytes */
TcsSortStatus tcs_sort(void *keys, size_t n, size_t es, void *scratch,
                       TcsSortCompare compare, void *ctx);

#endif

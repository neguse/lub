/* List.Sort の並べ替え。.NET の List<T>.Sort と同じ introsort (dotnet/runtime の ArraySortHelper<T>) で、
   同値の要素の並びと比較の呼び出し列 (回数・順・引数) まで同じ (il-spec §13)。
   tcsvm が使う。tcs2c は同じ手順を生成 C に持つ (doc/vm-design.md §15.1 で共有に寄せる)。 */
#ifndef TCS_SORT_H
#define TCS_SORT_H

#include <stddef.h>

/* 比較。left < right なら負、等しければ 0、大きければ正。
   比較の中で止まる理由 (fault) が起きたら *stop を 0 以外にする。以後の比較は呼ばれない */
typedef int (*TcsSortCompare)(void *ctx, const void *left, const void *right, int *stop);

typedef enum TcsSortStatus {
  TCS_SORT_OK = 0,
  TCS_SORT_STOPPED = 1,      /* 比較が *stop を立てた */
  TCS_SORT_INCONSISTENT = 2, /* 比較が不整合で走査が区間の外に出た (.NET は例外) */
} TcsSortStatus;

/* keys[0..n) (要素は es byte) を並べる。scratch は 2 * es byte の作業領域 */
TcsSortStatus tcs_sort(void *keys, size_t n, size_t es, void *scratch, TcsSortCompare compare,
                       void *ctx);

#endif

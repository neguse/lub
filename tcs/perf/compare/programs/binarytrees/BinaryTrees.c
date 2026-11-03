#include <stdio.h>
#include <stdlib.h>

// 確保は malloc、解放は木ごとに free (GC の無い言語の素直な書き方)
typedef struct Node {
  struct Node *left, *right;
} Node;

static Node *make(int depth) {
  Node *n = malloc(sizeof(Node));
  if (depth == 0) {
    n->left = n->right = NULL;
  } else {
    n->left = make(depth - 1);
    n->right = make(depth - 1);
  }
  return n;
}

static int check(const Node *n) {
  if (!n->left)
    return 1;
  return 1 + check(n->left) + check(n->right);
}

static void release(Node *n) {
  if (n->left) {
    release(n->left);
    release(n->right);
  }
  free(n);
}

static int run(int n, int print) {
  int minDepth = 4;
  int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

  int stretch = maxDepth + 1;
  Node *t = make(stretch);
  int stretchCheck = check(t);
  release(t);
  if (print)
    printf("stretch tree of depth %d\t check: %d\n", stretch, stretchCheck);
  int digest = stretchCheck;

  Node *longLived = make(maxDepth);
  for (int d = minDepth; d <= maxDepth; d += 2) {
    int iterations = 1 << (maxDepth - d + minDepth);
    int c = 0;
    for (int i = 0; i < iterations; i++) {
      Node *a = make(d);
      c += check(a);
      release(a);
    }
    if (print)
      printf("%d\t trees of depth %d\t check: %d\n", iterations, d, c);
    digest += c;
  }
  int longCheck = check(longLived);
  release(longLived);
  if (print)
    printf("long lived tree of depth %d\t check: %d\n", maxDepth, longCheck);
  return digest + longCheck;
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 10;
  // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
  const char *rs = getenv("BENCH_REPS");
  int reps = rs ? atoi(rs) : 1;
  int prev = 0;
  for (int r = 0; r < reps; r++)
    prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
  return 0;
}

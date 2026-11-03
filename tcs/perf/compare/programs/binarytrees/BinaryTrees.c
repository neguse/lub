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

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 10;
  int minDepth = 4;
  int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

  int stretch = maxDepth + 1;
  Node *t = make(stretch);
  printf("stretch tree of depth %d\t check: %d\n", stretch, check(t));
  release(t);

  Node *longLived = make(maxDepth);
  for (int d = minDepth; d <= maxDepth; d += 2) {
    int iterations = 1 << (maxDepth - d + minDepth);
    int c = 0;
    for (int i = 0; i < iterations; i++) {
      Node *a = make(d);
      c += check(a);
      release(a);
    }
    printf("%d\t trees of depth %d\t check: %d\n", iterations, d, c);
  }
  printf("long lived tree of depth %d\t check: %d\n", maxDepth, check(longLived));
  release(longLived);
  return 0;
}

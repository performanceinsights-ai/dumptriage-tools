/* A CPU burner with named functions, so a capture of it shows whether frames resolve to names (#41). */
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

static volatile unsigned long sink;

__attribute__((noinline)) static unsigned long dt_burn_inner(unsigned long n) {
    unsigned long x = n;
    for (unsigned long i = 0; i < 2000000UL; i++)
        x = x * 6364136223846793005UL + 1442695040888963407UL;
    return x;
}

__attribute__((noinline)) static void dt_burn_outer(void) {
    for (unsigned long n = 0;; n++)
        sink += dt_burn_inner(n);
}

int main(int argc, char **argv) {
    int seconds = argc > 1 ? atoi(argv[1]) : 600;
    alarm(seconds);
    printf("%d\n", getpid());
    fflush(stdout);
    dt_burn_outer();
    return 0;
}

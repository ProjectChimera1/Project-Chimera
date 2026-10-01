// Scratch: prove a plain C++ console can LoadLibrary a NativeAOT shared library and call an export.
#include <windows.h>
#include <cstdio>
typedef int (*add_fn)(int, int);
int main(int argc, char** argv) {
    HMODULE h = LoadLibraryA(argv[1]);
    if (!h) { printf("load failed %lu\n", GetLastError()); return 2; }
    add_fn f = (add_fn)GetProcAddress(h, "smoke_add");
    if (!f) { printf("no export\n"); return 3; }
    printf("smoke_add(40,2)=%d\n", f(40, 2));
    return 0;
}

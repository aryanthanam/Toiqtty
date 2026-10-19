// Build: .tools/zig-x86_64-windows-0.16.0/zig.exe cc -O2 flood.c -o flood.exe
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

static unsigned int terminal_columns(HANDLE output)
{
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (GetConsoleScreenBufferInfo(output, &info)) {
        int width = info.srWindow.Right - info.srWindow.Left + 1;
        if (width > 0)
            return (unsigned int)width;
    }
    return 126; // Redirected stdout has no terminal dimensions.
}

int main(void)
{
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    LARGE_INTEGER frequency, start, now;
    char *block = NULL;
    DWORD block_size = 0;
    unsigned int columns = 0;
    LONGLONG next_width_check = 0;
    const char alphabet[] = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    unsigned long long bytes = 0;

    if (output == NULL || output == INVALID_HANDLE_VALUE ||
        !QueryPerformanceFrequency(&frequency) || !QueryPerformanceCounter(&start)) {
        fputs("Unable to initialize output or timer.\n", stderr);
        return 1;
    }

    for (;;) {
        QueryPerformanceCounter(&now);
        if (now.QuadPart - start.QuadPart >= frequency.QuadPart * 3)
            break;
        // Poll at 20 Hz so resizing works without querying the console per write.
        if (now.QuadPart >= next_width_check) {
            unsigned int width = terminal_columns(output);
            next_width_check = now.QuadPart + frequency.QuadPart / 20;
            if (width != columns) {
                DWORD size = (width + 2) * 32;
                char *resized = realloc(block, size);
                if (!resized) {
                    free(block);
                    fputs("Unable to allocate output buffer.\n", stderr);
                    return 1;
                }
                block = resized;
                block_size = size;
                columns = width;
                for (unsigned int row = 0; row < 32; ++row) {
                    char *line = block + row * (columns + 2);
                    for (unsigned int col = 0; col < columns; ++col)
                        line[col] = alphabet[(row * 7 + col) % (sizeof(alphabet) - 1)];
                    line[columns] = '\r';
                    line[columns + 1] = '\n';
                }
            }
        }
        DWORD written = 0;
        if (!WriteFile(output, block, block_size, &written, NULL) || written == 0) {
            free(block);
            fputs("Output closed or failed.\n", stderr);
            return 1;
        }
        bytes += written;
    }

    free(block);
    QueryPerformanceCounter(&now);
    double seconds = (double)(now.QuadPart - start.QuadPart) / frequency.QuadPart;
    fprintf(stderr, "\nFlood complete: %.2f seconds, %.2f MiB, %.2f MiB/s.\n",
            seconds, bytes / 1048576.0, bytes / 1048576.0 / seconds);
    return 0;
}

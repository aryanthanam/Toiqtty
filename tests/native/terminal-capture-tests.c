#define TOIQTTY_BUILD
#include "toiqtty.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int checks;
static void require(int condition, const char* message) {
    if (!condition) { fprintf(stderr, "FAIL: %s\n", message); exit(1); }
    checks++;
}
static const uint8_t* text(const OtCell* cell) {
    return cell->length > OT_CELL_TEXT_BYTES ? cell->extended_text : cell->text;
}
static void compare(const OtCell* first, const OtCell* second, size_t count) {
    for (size_t index = 0; index < count; index++) {
        const OtCell* a = first + index;
        const OtCell* b = second + index;
        require(a->foreground == b->foreground && a->background == b->background && a->flags == b->flags
            && a->length == b->length && a->underline_color == b->underline_color
            && a->underline_style == b->underline_style && a->width == b->width, "cell attributes match a full capture");
        require(!a->length || (text(a) && text(b) && !memcmp(text(a), text(b), a->length)),
            "row arena text matches a full capture after growth or an unchanged row");
    }
}
int main(void) {
    const size_t capacity = 120 * 40;
    OtCell* incremental = calloc(capacity, sizeof(*incremental));
    OtCell* reference = calloc(capacity, sizeof(*reference));
    OtTerminal* first = ot_create(80, 24, NULL, NULL);
    OtTerminal* second = ot_create(80, 24, NULL, NULL);
    require(incremental && reference && first && second, "capture owners are created");
    require(ot_cell_layout() == (sizeof(OtCell) | (offsetof(OtCell, text) << 8) | (OT_CELL_TEXT_BYTES << 16)),
        "bridge layout descriptor matches the native record");
    ot_incremental(first, 1);
    require(ot_scrollback_limits(first, 0, 0) == 0 && ot_scrollback_limits(second, 0, 0) == 0, "history is bounded");
    char grapheme[130];
    grapheme[0] = 'a';
    for (int index = 0; index < 64; index++) { grapheme[1 + index * 2] = (char)0xcc; grapheme[2 + index * 2] = (char)0x81; }
    grapheme[129] = 0;
    for (int step = 0; step < 360; step++) {
        int geometry = step / 12 % 3;
        uint16_t columns = geometry == 0 ? 80 : geometry == 1 ? 100 : 120;
        uint16_t rows = geometry == 0 ? 24 : geometry == 1 ? 30 : 40;
        if (step % 12 == 0) {
            require(ot_resize(first, columns, rows, 9, 18) == 0 && ot_resize(second, columns, rows, 9, 18) == 0,
                "resize changes both capture geometries");
            require(ot_capture_text_bytes(first) == 0, "resize releases row arenas and offset scratch");
        }
        char input[4096];
        int length;
        if (step % 6 == 0) {
            length = snprintf(input, sizeof(input), "\x1b[2J\x1b[H");
            for (int index = 0; index < 24; index++) {
                memcpy(input + length, grapheme, 129); length += 129;
            }
        } else if (step % 6 == 1) length = snprintf(input, sizeof(input), "\x1b[5;1Hunchanged row above %d", step);
        else if (step % 6 == 2) length = snprintf(input, sizeof(input), "\x1b[2J\x1b[Hplain");
        else length = snprintf(input, sizeof(input), "\x1b[%d;3H\x1b[1;3;4:3;38;2;120;180;240m%s\x1b[0m", 2 + step % 20, grapheme);
        require(length > 0 && (size_t)length < sizeof(input), "generated input fits its call-owned buffer");
        ot_write(first, (const uint8_t*)input, (size_t)length);
        ot_write(second, (const uint8_t*)input, (size_t)length);
        OtFrame a, b;
        int count = ot_frame(first, &a, incremental, capacity);
        require(count > 0 && ot_frame(second, &b, reference, capacity) == count, "captures fit their caller-owned records");
        require(a.columns == b.columns && a.rows == b.rows && a.cursor_x == b.cursor_x && a.cursor_y == b.cursor_y,
            "incremental frame geometry matches full capture");
        compare(incremental, reference, (size_t)count);
    }
    ot_destroy(first); ot_destroy(second); free(incremental); free(reference);
    printf("%d native terminal capture checks passed.\n", checks);
    return 0;
}

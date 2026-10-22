#ifndef TOIQTTY_H
#define TOIQTTY_H
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifdef TOIQTTY_BUILD
#define OT_API __declspec(dllexport)
#else
#define OT_API __declspec(dllimport)
#endif
typedef struct OtTerminal OtTerminal;
#define OT_CELL_TEXT_BYTES 32
// All calls for one terminal must be serialized by the caller.
typedef void (*OtEffect)(void* context, int kind, const uint8_t* data, size_t length);
typedef struct { const uint8_t* ptr; size_t len; } OtString;
typedef struct { OtString mime, data; } OtClipboardContent;
typedef bool (*OtWriterFn)(void*, const uint8_t*, size_t);
typedef struct { OtWriterFn write; void* context; } OtWriter;
typedef bool (*OtMimeReader)(void*, OtString, OtWriter);
typedef struct {
    int32_t state, detail, progress, has_exit_code, exit_code;
    OtString id, app, title, body;
} OtEvent;
typedef struct {
    const void* request;
    int32_t write, location, list, granted, can_remember;
    const OtClipboardContent* contents;
    size_t contents_len;
    const OtString* mimes;
    size_t mimes_len;
    OtString name;
} OtClipboard;
typedef struct {
    uint32_t foreground, background, flags, length;
    uint32_t underline_color;
    // Upstream underline variants are 0..5; cell width is 0, 1, or 2.
    // Keep 32 text bytes while fitting the private transfer record in 64 bytes.
    uint8_t underline_style, width;
    const uint8_t* extended_text;
    uint8_t text[OT_CELL_TEXT_BYTES];
} OtCell;
// Packed byte-size, inline-text offset, and inline capacity (8 bits each).
// Validate before a managed caller allocates an array of these private records.
OT_API uint32_t ot_cell_layout(void);
OT_API uint64_t ot_capture_text_bytes(OtTerminal* terminal);
typedef struct {
    uint32_t columns, rows, cursor_x, cursor_y, cursor_visible, cursor_style;
    uint32_t foreground, background;
    uint32_t cursor_color, cursor_blinking, render_held, cell_width, cell_height;
    uint32_t animation_delay;
    uint32_t cursor_wide_tail, changed_rows;
} OtFrame;
typedef struct {
    uint64_t generation;
    uint32_t image_id, placement_id, width, height, format;
    int32_t z, column, row;
    uint32_t offset_x, offset_y, pixel_width, pixel_height;
    uint32_t source_x, source_y, source_width, source_height;
    const uint8_t* pixels;
    size_t length;
} OtGraphic;
OT_API OtTerminal* ot_create(uint16_t columns, uint16_t rows, OtEffect effect, void* context);
OT_API void ot_destroy(OtTerminal* terminal);
OT_API void ot_identity(OtTerminal* terminal, const uint8_t* identity, size_t length);
// -1 removes a limit; zero bytes disables and clears scrollback.
OT_API int ot_scrollback_limits(OtTerminal* terminal, int64_t bytes, int64_t lines);
// A zero foreground restores defaults. Palette is NULL or exactly 16 colors.
OT_API int ot_theme(OtTerminal* terminal, uint32_t foreground, uint32_t background, uint32_t cursor, const uint32_t* palette, size_t count);
OT_API int ot_palette_entries(OtTerminal* terminal, const uint32_t* indices, const uint32_t* colors, size_t count);
OT_API int ot_cursor_defaults(OtTerminal* terminal, uint32_t style, bool blink);
OT_API void ot_write(OtTerminal* terminal, const uint8_t* data, size_t length);
OT_API int ot_resize(OtTerminal* terminal, uint16_t columns, uint16_t rows, uint32_t cell_width, uint32_t cell_height);
OT_API int ot_frame(OtTerminal* terminal, OtFrame* frame, OtCell* cells, size_t capacity);
// Incremental mode requires the same cell buffer until resize; dirty flags per viewport row.
OT_API void ot_incremental(OtTerminal* terminal, int enabled);
// Kitty graphics t=f: whether programs may name local files to display.
OT_API void ot_image_files(OtTerminal* terminal, int enabled);
OT_API int ot_row_changed(OtTerminal* terminal, uint32_t row);
OT_API void ot_clipboard_reply(const OtClipboard* request, int result, const OtClipboardContent* contents, size_t count, const OtString* available, size_t available_count, int remember);
OT_API int ot_clipboard_paste(OtTerminal* terminal, const uint8_t* text, size_t length, int clipboard);
OT_API int ot_clipboard_paste_mimes(OtTerminal* terminal, const OtString* mimes, size_t count, OtMimeReader reader, void* context);
OT_API int ot_drop(OtTerminal* terminal, int action, int x, int y, uint32_t operations, const OtClipboardContent* items, size_t count);
// Borrowed snapshot, valid until the next call that mutates this terminal.
OT_API int ot_graphics(OtTerminal* terminal, const OtGraphic** graphics);
OT_API uint64_t ot_graphics_cache_bytes(OtTerminal* terminal);
OT_API int ot_hyperlink(OtTerminal* terminal, uint16_t column, uint16_t row, uint8_t* output, size_t capacity);
OT_API void ot_scroll(OtTerminal* terminal, int delta);
OT_API int ot_scrollbar(OtTerminal* terminal, uint64_t* total, uint64_t* offset, uint64_t* visible);
OT_API void ot_scroll_to(OtTerminal* terminal, uint64_t row);
OT_API int ot_key(OtTerminal* terminal, uint32_t virtual_key, uint32_t modifiers, int action, uint32_t unshifted, uint32_t consumed_modifiers, const char* text, size_t length, char* output, size_t capacity);
OT_API int ot_paste(OtTerminal* terminal, char* text, size_t length, char* output, size_t capacity);
OT_API int ot_metadata(OtTerminal* terminal, int kind, uint8_t* output, size_t capacity);
OT_API int ot_mouse(OtTerminal* terminal, int action, int button, uint32_t modifiers, float x, float y, char* output, size_t capacity);
OT_API int ot_mouse_tracking(OtTerminal* terminal);
OT_API void ot_focus(OtTerminal* terminal, int focused);
OT_API void ot_select(OtTerminal* terminal, int start, int end);
OT_API int ot_gesture(OtTerminal* terminal, int action, float x, float y, int rectangle);
OT_API int ot_copy(OtTerminal* terminal, uint8_t* output, size_t capacity);
OT_API int ot_find(OtTerminal* terminal, const uint8_t* text, size_t length, int direction, size_t* count, size_t* index);
#endif

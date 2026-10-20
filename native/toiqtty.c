#define TOIQTTY_BUILD
#include "toiqtty.h"
#include <ghostty/vt.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

bool ot_initialize_codecs(void);
typedef struct OtImage {
    uint64_t generation;
    uint8_t* pixels;
    size_t length;
    bool used;
    struct OtImage* next;
} OtImage;
typedef struct { uint8_t* bytes; size_t used, capacity; } OtTextRow;

struct OtTerminal {
    GhosttyTerminal terminal;
    GhosttyColorRgb original_palette[256];
    GhosttyRenderState render;
    GhosttyRenderStateRowIterator iterator;
    GhosttyRenderStateRowCells cells;
    GhosttyKeyEncoder encoder;
    GhosttyKeyEvent event;
    GhosttyMouseEncoder mouse_encoder;
    GhosttyMouseEvent mouse_event;
    GhosttySearch search;
    GhosttyKittyGraphicsPlacementIterator placements;
    GhosttySelectionGesture gesture;
    GhosttySelectionGestureEvent gesture_events[4];
    OtTextRow* text_rows;
    size_t text_row_count, text_columns;
    size_t* text_offsets;
    bool incremental, force_full;
    uint8_t* row_changed;
    size_t row_capacity;
    OtGraphic* graphics;
    size_t graphics_count, graphics_capacity;
    OtImage* images;
    bool render_held;
    ULONGLONG hold_started;
    uint32_t cell_width, cell_height;
    uint32_t animation_delay;
    uint8_t identity[128];
    size_t identity_length;
    OtEffect effect;
    void* context;
};
_Static_assert(sizeof(OtCell) < 256 && offsetof(OtCell, text) < 256 && OT_CELL_TEXT_BYTES < 256, "Cell layout descriptor overflow");
uint32_t ot_cell_layout(void) { return (uint32_t)sizeof(OtCell) | ((uint32_t)offsetof(OtCell, text) << 8) | (OT_CELL_TEXT_BYTES << 16); }
static void release_capture_text(OtTerminal* self) {
    for (size_t row = 0; row < self->text_row_count; row++) free(self->text_rows[row].bytes);
    free(self->text_rows); free(self->text_offsets);
    self->text_rows = NULL; self->text_offsets = NULL; self->text_row_count = self->text_columns = 0;
}
static bool reserve_row_text(OtTextRow* row, size_t length) {
    if (length > SIZE_MAX - row->used) return false;
    size_t needed = row->used + length;
    if (needed <= row->capacity) return true;
    size_t capacity = row->capacity && row->capacity <= SIZE_MAX / 2 ? row->capacity * 2 : 256;
    if (capacity < needed) capacity = needed;
    uint8_t* bytes = realloc(row->bytes, capacity);
    if (!bytes) return false;
    row->bytes = bytes; row->capacity = capacity; return true;
}
static void trim_row_text(OtTextRow* row) {
    // Retain small reusable storage; release a previous large grapheme peak.
    if (!row->used && row->capacity > 256) { free(row->bytes); *row = (OtTextRow){0}; return; }
    if (row->used <= SIZE_MAX / 2 && row->capacity > 256 && row->capacity > row->used * 2) {
        size_t capacity = row->used * 2; if (capacity < 256) capacity = 256;
        uint8_t* bytes = realloc(row->bytes, capacity);
        if (bytes) { row->bytes = bytes; row->capacity = capacity; }
    }
}
uint64_t ot_capture_text_bytes(OtTerminal* self) {
    if (!self) return 0;
    uint64_t bytes = self->text_row_count * sizeof(*self->text_rows) + self->text_columns * sizeof(*self->text_offsets);
    for (size_t row = 0; row < self->text_row_count; row++) bytes += self->text_rows[row].capacity;
    return bytes;
}
static uint32_t rgb(GhosttyColorRgb c) { return 0xff000000u | (c.r << 16) | (c.g << 8) | c.b; }
// Narrow additions to the pinned library, implemented using upstream algorithms.
__declspec(dllimport) uint32_t ghostty_toiqtty_graphics_tick(GhosttyTerminal terminal, uint64_t now_ms);
__declspec(dllimport) int32_t ghostty_toiqtty_dnd(GhosttyTerminal terminal, uint32_t action, int32_t x, int32_t y, uint32_t width, uint32_t height, uint32_t operations, const OtClipboardContent* items, size_t count);
typedef void (*OtDropEffect)(GhosttyTerminal terminal, void* context, uint32_t event);
__declspec(dllimport) void ghostty_toiqtty_dnd_subscribe(GhosttyTerminal terminal, OtDropEffect callback);
typedef void (*VirtualGraphic)(void*, uint32_t, uint32_t, int32_t, int32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t);
__declspec(dllimport) void ghostty_toiqtty_graphics_virtual(GhosttyTerminal terminal, uint32_t width, uint32_t height, void* context, VirtualGraphic callback);
static void append_graphic(OtTerminal* self, GhosttyKittyGraphicsImage image, OtGraphic graphic) {
    ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_WIDTH, &graphic.width);
    ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_HEIGHT, &graphic.height);
    GhosttyKittyImageFormat format = GHOSTTY_KITTY_IMAGE_FORMAT_RGBA;
    ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_FORMAT, &format);
    switch (format) {
        case GHOSTTY_KITTY_IMAGE_FORMAT_RGB: graphic.format = 3; break;
        case GHOSTTY_KITTY_IMAGE_FORMAT_RGBA: graphic.format = 4; break;
        case GHOSTTY_KITTY_IMAGE_FORMAT_GRAY_ALPHA: graphic.format = 2; break;
        case GHOSTTY_KITTY_IMAGE_FORMAT_GRAY: graphic.format = 1; break;
        default: return;
    }
    ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_GENERATION, &graphic.generation);
    ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_DATA_LEN, &graphic.length);
    const uint8_t* pixels = NULL;
    if (ghostty_kitty_graphics_image_get(image, GHOSTTY_KITTY_IMAGE_DATA_DATA_PTR, &pixels) != GHOSTTY_SUCCESS || !pixels || !graphic.length) return;
    OtImage* cached = self->images;
    while (cached && cached->generation != graphic.generation) cached = cached->next;
    if (!cached) {
        cached = calloc(1, sizeof(*cached));
        if (!cached) return;
        cached->pixels = malloc(graphic.length);
        if (!cached->pixels) { free(cached); return; }
        memcpy(cached->pixels, pixels, graphic.length);
        cached->generation = graphic.generation; cached->length = graphic.length;
        cached->next = self->images; self->images = cached;
    }
    cached->used = true; graphic.pixels = cached->pixels;
    if (self->graphics_count == self->graphics_capacity) {
        size_t capacity = self->graphics_capacity ? self->graphics_capacity * 2 : 16;
        OtGraphic* grown = realloc(self->graphics, capacity * sizeof(*grown));
        if (!grown) return;
        self->graphics = grown; self->graphics_capacity = capacity;
    }
    self->graphics[self->graphics_count++] = graphic;
}
static void virtual_graphic(void* context, uint32_t image_id, uint32_t placement_id, int32_t column, int32_t row,
        uint32_t offset_x, uint32_t offset_y, uint32_t width, uint32_t height,
        uint32_t source_x, uint32_t source_y, uint32_t source_width, uint32_t source_height) {
    OtTerminal* self = context; GhosttyKittyGraphics storage = NULL;
    if (ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_KITTY_GRAPHICS, &storage) != GHOSTTY_SUCCESS) return;
    OtGraphic graphic = {0};
    graphic.image_id = image_id; graphic.placement_id = placement_id; graphic.column = column; graphic.row = row;
    graphic.offset_x = offset_x; graphic.offset_y = offset_y; graphic.pixel_width = width; graphic.pixel_height = height;
    graphic.source_x = source_x; graphic.source_y = source_y; graphic.source_width = source_width; graphic.source_height = source_height;
    graphic.z = -1; // Matches Ghostty's Unicode-placeholder render layer.
    GhosttyKittyGraphicsImage image = ghostty_kitty_graphics_image(storage, image_id);
    if (image) append_graphic(self, image, graphic);
}
static void capture_graphics(OtTerminal* self) {
    self->graphics_count = 0;
    for (OtImage* i = self->images; i; i = i->next) i->used = false;
    GhosttyKittyGraphics storage = NULL;
    if (ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_KITTY_GRAPHICS, &storage) == GHOSTTY_SUCCESS && storage &&
        ghostty_kitty_graphics_get(storage, GHOSTTY_KITTY_GRAPHICS_DATA_PLACEMENT_ITERATOR, &self->placements) == GHOSTTY_SUCCESS) {
        while (ghostty_kitty_graphics_placement_next(self->placements)) {
            OtGraphic graphic = {0};
            ghostty_kitty_graphics_placement_get(self->placements, GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_IMAGE_ID, &graphic.image_id);
            ghostty_kitty_graphics_placement_get(self->placements, GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_PLACEMENT_ID, &graphic.placement_id);
            ghostty_kitty_graphics_placement_get(self->placements, GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_Z, &graphic.z);
            ghostty_kitty_graphics_placement_get(self->placements, GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_X_OFFSET, &graphic.offset_x);
            ghostty_kitty_graphics_placement_get(self->placements, GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_Y_OFFSET, &graphic.offset_y);
            GhosttyKittyGraphicsImage image = ghostty_kitty_graphics_image(storage, graphic.image_id);
            if (!image) continue;
            GhosttyKittyGraphicsPlacementRenderInfo info = GHOSTTY_INIT_SIZED(GhosttyKittyGraphicsPlacementRenderInfo);
            if (ghostty_kitty_graphics_placement_render_info(self->placements, image, self->terminal, &info) != GHOSTTY_SUCCESS || !info.viewport_visible) continue;
            graphic.column = info.viewport_col; graphic.row = info.viewport_row;
            graphic.pixel_width = info.pixel_width; graphic.pixel_height = info.pixel_height;
            graphic.source_x = info.source_x; graphic.source_y = info.source_y;
            graphic.source_width = info.source_width; graphic.source_height = info.source_height;
            append_graphic(self, image, graphic);
        }
        ghostty_toiqtty_graphics_virtual(self->terminal, self->cell_width, self->cell_height, self, virtual_graphic);
    }
    OtImage** link = &self->images;
    while (*link) {
        OtImage* image = *link;
        if (image->used) link = &image->next;
        else { *link = image->next; free(image->pixels); free(image); }
    }
}
static void render_hold(GhosttyTerminal terminal, void* context, bool held) {
    OtTerminal* self = context;
    if (held) {
        ghostty_render_state_update(self->render, terminal);
        capture_graphics(self);
        self->hold_started = GetTickCount64();
    }
    self->render_held = held;
}
static void end_hold(OtTerminal* self) {
    GhosttyTerminalModeConfig mode = {GHOSTTY_MODE_SYNC_OUTPUT, false};
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_MODE, &mode);
    self->render_held = false;
}
static bool color_scheme(GhosttyTerminal t, void* context, GhosttyColorScheme* scheme) {
    (void)context;
    GhosttyColorRgb bg = {19,21,25};
    ghostty_terminal_get(t, GHOSTTY_TERMINAL_DATA_COLOR_BACKGROUND, &bg);
    *scheme = (bg.r * .2126 + bg.g * .7152 + bg.b * .0722) > 140.25 ? GHOSTTY_COLOR_SCHEME_LIGHT : GHOSTTY_COLOR_SCHEME_DARK;
    return true;
}
static bool report_size(GhosttyTerminal terminal, void* context, GhosttySizeReportSize* size) {
    OtTerminal* self = context;
    uint16_t columns = 0, rows = 0;
    ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_COLS, &columns);
    ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows);
    *size = (GhosttySizeReportSize){rows, columns, self->cell_width, self->cell_height};
    return true;
}
static GhosttyString xtversion(GhosttyTerminal terminal, void* context) {
    (void)terminal; OtTerminal* self = context;
    return (GhosttyString){self->identity, self->identity_length};
}
static bool device_attributes(GhosttyTerminal terminal, void* context, GhosttyDeviceAttributes* attributes) {
    (void)terminal; (void)context;
    memset(attributes, 0, sizeof(*attributes));
    attributes->primary.conformance_level = 62;
    attributes->primary.features[0] = GHOSTTY_DA_FEATURE_ANSI_COLOR;
    attributes->primary.features[1] = GHOSTTY_DA_FEATURE_SELECTIVE_ERASE;
    attributes->primary.num_features = 2;
    attributes->secondary.device_type = GHOSTTY_DA_DEVICE_TYPE_VT220;
    attributes->tertiary.unit_id = 0x544f4951; // TOIQ
    return true;
}
static void write_pty(GhosttyTerminal t, void* context, const uint8_t* data, size_t length) {
    (void)t; OtTerminal* self = context;
    if (self->effect) self->effect(self->context, 0, data, length);
}
static void bell(GhosttyTerminal t, void* context) {
    (void)t; OtTerminal* self = context;
    if (self->effect) self->effect(self->context, 1, NULL, 0);
}
static void notification(GhosttyTerminal t, void* context, const GhosttyTerminalDesktopNotification* n) {
    (void)t; OtTerminal* self = context;
    OtEvent event = {0}; event.title = (OtString){n->title.ptr, n->title.len}; event.body = (OtString){n->body.ptr, n->body.len};
    if (self->effect) self->effect(self->context, 2, (const uint8_t*)&event, sizeof(event));
}
static void progress_report(GhosttyTerminal t, void* context, const GhosttyTerminalProgressReport* r) {
    (void)t; OtTerminal* self = context;
    OtEvent event = {0}; event.state = r->state; event.progress = r->progress;
    if (self->effect) self->effect(self->context, 3, (const uint8_t*)&event, sizeof(event));
}
static void program_status(GhosttyTerminal t, void* context, const GhosttyTerminalProgramStatus* r) {
    (void)t; OtTerminal* self = context;
    OtEvent event = {0}; event.state = r->state; event.detail = r->kind; event.progress = r->progress;
    event.id = (OtString){r->id.ptr, r->id.len}; event.app = (OtString){r->app.ptr, r->app.len};
    event.title = (OtString){r->title.ptr, r->title.len}; event.body = (OtString){r->message.ptr, r->message.len};
    if (self->effect) self->effect(self->context, 4, (const uint8_t*)&event, sizeof(event));
}
static void semantic_prompt(GhosttyTerminal t, void* context, const GhosttyTerminalSemanticPrompt* r) {
    (void)t; OtTerminal* self = context;
    OtEvent event = {0}; event.state = r->kind; event.detail = r->prompt_kind;
    event.has_exit_code = r->has_exit_code; event.exit_code = r->exit_code;
    event.title = (OtString){r->command.ptr, r->command.len}; event.body = (OtString){r->error.ptr, r->error.len};
    if (self->effect) self->effect(self->context, 5, (const uint8_t*)&event, sizeof(event));
}
static void reset(GhosttyTerminal t, void* context) {
    (void)t; OtTerminal* self = context; self->force_full = true;
    ghostty_selection_gesture_reset(self->gesture, self->terminal);
    if (self->effect) self->effect(self->context, 8, NULL, 0);
}
static void drop_effect(GhosttyTerminal t, void* context, uint32_t kind) {
    (void)t; OtTerminal* self = context;
    if (self->effect) self->effect(self->context, 9, (const uint8_t*)&kind, sizeof(kind));
}
static void clipboard_write(GhosttyTerminal t, void* context, const GhosttyClipboardWrite* r) {
    (void)t; OtTerminal* self = context;
    OtClipboard request = {0}; request.request = r; request.write = 1; request.location = r->location;
    request.granted = r->granted; request.can_remember = r->can_remember;
    request.contents = (const OtClipboardContent*)r->contents; request.contents_len = r->contents_len;
    request.name = (OtString){r->name.ptr, r->name.len};
    if (self->effect) self->effect(self->context, 6, (const uint8_t*)&request, sizeof(request));
}
static void clipboard_read(GhosttyTerminal t, void* context, const GhosttyClipboardRead* r) {
    (void)t; OtTerminal* self = context;
    OtClipboard request = {0}; request.request = r; request.location = r->location;
    request.list = r->list; request.granted = r->granted; request.can_remember = r->can_remember;
    request.mimes = (const OtString*)r->mimes; request.mimes_len = r->mimes_len;
    request.name = (OtString){r->name.ptr, r->name.len};
    if (self->effect) self->effect(self->context, 7, (const uint8_t*)&request, sizeof(request));
}
void ot_clipboard_reply(const OtClipboard* r, int result, const OtClipboardContent* contents, size_t count,
        const OtString* available, size_t available_count, int remember) {
    if (r->write) {
        const GhosttyClipboardWrite* write = r->request;
        GhosttyClipboardWriteReply reply = GHOSTTY_INIT_SIZED(GhosttyClipboardWriteReply);
        reply.result = result; reply.remember = remember != 0; write->reply(write, &reply);
    } else {
        const GhosttyClipboardRead* read = r->request;
        GhosttyClipboardReadReply reply = GHOSTTY_INIT_SIZED(GhosttyClipboardReadReply);
        reply.result = result; reply.remember = remember != 0;
        reply.contents = (const GhosttyClipboardContent*)contents; reply.contents_len = count;
        reply.available = (const GhosttyString*)available; reply.available_len = available_count;
        read->reply(read, &reply);
    }
}
void ot_destroy(OtTerminal* self) {
    if (!self) return;
    ghostty_selection_gesture_free(self->gesture, self->terminal);
    for (int i = 0; i < 4; i++) ghostty_selection_gesture_event_free(self->gesture_events[i]);
    release_capture_text(self);
    free(self->row_changed);
    ghostty_search_free(self->search);
    ghostty_kitty_graphics_placement_iterator_free(self->placements);
    free(self->graphics);
    while (self->images) { OtImage* image = self->images; self->images = image->next; free(image->pixels); free(image); }
    ghostty_mouse_event_free(self->mouse_event);
    ghostty_mouse_encoder_free(self->mouse_encoder);
    ghostty_key_event_free(self->event);
    ghostty_key_encoder_free(self->encoder);
    ghostty_render_state_row_cells_free(self->cells);
    ghostty_render_state_row_iterator_free(self->iterator);
    ghostty_render_state_free(self->render);
    ghostty_terminal_free(self->terminal);
    free(self);
}
OtTerminal* ot_create(uint16_t columns, uint16_t rows, OtEffect effect, void* context) {
    if (!ot_initialize_codecs()) return NULL;
    OtTerminal* self = calloc(1, sizeof(*self));
    if (!self) return NULL;
    self->effect = effect; self->context = context;
    if (ghostty_terminal_new(NULL, &self->terminal, columns, rows) != GHOSTTY_SUCCESS ||
        ghostty_render_state_new(NULL, &self->render) != GHOSTTY_SUCCESS ||
        ghostty_render_state_row_iterator_new(NULL, &self->iterator) != GHOSTTY_SUCCESS ||
        ghostty_render_state_row_cells_new(NULL, &self->cells) != GHOSTTY_SUCCESS ||
        ghostty_key_encoder_new(NULL, &self->encoder) != GHOSTTY_SUCCESS ||
        ghostty_key_event_new(NULL, &self->event) != GHOSTTY_SUCCESS ||
        ghostty_mouse_encoder_new(NULL, &self->mouse_encoder) != GHOSTTY_SUCCESS ||
        ghostty_mouse_event_new(NULL, &self->mouse_event) != GHOSTTY_SUCCESS ||
        ghostty_search_new(NULL, &self->search, self->terminal) != GHOSTTY_SUCCESS ||
        ghostty_kitty_graphics_placement_iterator_new(NULL, &self->placements) != GHOSTTY_SUCCESS) { ot_destroy(self); return NULL; }
    if (ghostty_selection_gesture_new(NULL, &self->gesture) != GHOSTTY_SUCCESS) { ot_destroy(self); return NULL; }
    if (ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_COLOR_PALETTE_DEFAULT, self->original_palette) != GHOSTTY_SUCCESS) { ot_destroy(self); return NULL; }
    // Match the standalone Ghostty app, rather than the tiny VT library default.
    if (ot_scrollback_limits(self, 50000000, -1) != GHOSTTY_SUCCESS) { ot_destroy(self); return NULL; }
    for (int i = 0; i < 4; i++)
        if (ghostty_selection_gesture_event_new(NULL, &self->gesture_events[i], i) != GHOSTTY_SUCCESS) { ot_destroy(self); return NULL; }
    GhosttyColorRgb bg = {19, 21, 25}, fg = {215, 220, 229};
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &bg);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &fg);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_USERDATA, self);
    ghostty_toiqtty_dnd_subscribe(self->terminal, drop_effect);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY, (const void*)write_pty);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_BELL, (const void*)bell);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION, (const void*)notification);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_PROGRESS_REPORT, (const void*)progress_report);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_PROGRAM_STATUS, (const void*)program_status);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT, (const void*)semantic_prompt);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_RESET, (const void*)reset);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE, (const void*)clipboard_write);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_READ, (const void*)clipboard_read);
    size_t clipboard_limit = 8u * 1024 * 1024;
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE_MAX_BYTES, &clipboard_limit);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_RENDER_HOLD, (const void*)render_hold);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_SCHEME, (const void*)color_scheme);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SIZE, (const void*)report_size);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_XTVERSION, (const void*)xtversion);
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_DEVICE_ATTRIBUTES, (const void*)device_attributes);
    uint64_t image_limit = 64u * 1024 * 1024;
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_STORAGE_LIMIT, &image_limit);
    // Validation and image loading stay in Ghostty, including canonical Windows
    // handle paths, regular-file checks, byte limits and guarded temp deletion.
    bool image_files = true;
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_FILE, &image_files);
    WCHAR temp_path[32768]; char temp_utf8[32768 * 3];
    DWORD temp_length = GetTempPathW(sizeof(temp_path) / sizeof(*temp_path), temp_path);
    if (temp_length > 0 && temp_length < sizeof(temp_path) / sizeof(*temp_path)) {
        int utf8_length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, temp_path, (int)temp_length,
            temp_utf8, sizeof(temp_utf8), NULL, NULL);
        if (utf8_length > 0) {
            GhosttyString directory = {(const uint8_t*)temp_utf8, (size_t)utf8_length};
            if (ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_TEMP_FILE, &directory) != GHOSTTY_SUCCESS) {
                ot_destroy(self); return NULL;
            }
        }
    }
    // ConPTY cannot pull scrollback back into its own screen on resize.
    bool pull_scrollback = false;
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_RESIZE_PULL_SCROLLBACK, &pull_scrollback);
    GhosttyString terminfo = {(const uint8_t*)"xterm-256color", 14};
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_TERMINFO_NAME, &terminfo);
    ot_resize(self, columns, rows, 8, 21);
    return self;
}
int ot_scrollback_limits(OtTerminal* self, int64_t bytes, int64_t lines) {
    if (bytes < -1 || lines < -1) return -1;
    size_t max_bytes = (size_t)bytes, max_lines = (size_t)lines;
    GhosttyResult result = ghostty_terminal_set(self->terminal,
        GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_BYTES, bytes == -1 ? NULL : &max_bytes);
    if (result != GHOSTTY_SUCCESS) return result;
    result = ghostty_terminal_set(self->terminal,
        GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, lines == -1 ? NULL : &max_lines);
    self->force_full = true;
    return result;
}
void ot_identity(OtTerminal* self, const uint8_t* identity, size_t length) {
    self->identity_length = length < sizeof(self->identity) ? length : sizeof(self->identity);
    memcpy(self->identity, identity, self->identity_length);
}
int ot_theme(OtTerminal* self, uint32_t foreground, uint32_t background, uint32_t cursor, const uint32_t* colors, size_t count) {
    if ((colors && count != 16) || (!colors && count != 0)) return -1;
    GhosttyColorRgb fg = {(foreground >> 16) & 255, (foreground >> 8) & 255, foreground & 255};
    GhosttyColorRgb bg = {(background >> 16) & 255, (background >> 8) & 255, background & 255};
    GhosttyColorRgb cr = {(cursor >> 16) & 255, (cursor >> 8) & 255, cursor & 255};
    if (!foreground) { fg = (GhosttyColorRgb){215,220,229}; bg = (GhosttyColorRgb){19,21,25}; }
    GhosttyColorRgb palette[256];
    if (colors) {
        memcpy(palette, self->original_palette, sizeof(palette));
        for (size_t i = 0; i < 16; i++) palette[i] = (GhosttyColorRgb){(colors[i] >> 16) & 255, (colors[i] >> 8) & 255, colors[i] & 255};
    }
    GhosttyResult result = ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &fg);
    if (result != GHOSTTY_SUCCESS) return result;
    result = ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &bg);
    if (result != GHOSTTY_SUCCESS) return result;
    result = ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, foreground ? &cr : NULL);
    if (result != GHOSTTY_SUCCESS) return result;
    result = ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, colors ? palette : NULL);
    self->force_full = true;
    return result;
}
void ot_write(OtTerminal* self, const uint8_t* data, size_t length) { ghostty_terminal_vt_write(self->terminal, data, length); }
int ot_palette_entries(OtTerminal* self, const uint32_t* indices, const uint32_t* colors, size_t count) {
    if (count > 240 || (count && (!indices || !colors))) return -1;
    GhosttyColorRgb palette[256];
    GhosttyResult result = ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_COLOR_PALETTE_DEFAULT, palette);
    if (result != GHOSTTY_SUCCESS) return result;
    for (size_t i=0; i<count; i++) {
        if (indices[i] < 16 || indices[i] > 255) return -1;
        uint32_t color = colors[i];
        palette[indices[i]] = (GhosttyColorRgb){(color >> 16) & 255, (color >> 8) & 255, color & 255};
    }
    self->force_full = true;
    return ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, palette);
}
int ot_cursor_defaults(OtTerminal* self, uint32_t style, bool blink) {
    if (style > 3) return -1;
    GhosttyTerminalCursorStyle cursor_style = (GhosttyTerminalCursorStyle)style;
    GhosttyResult result = ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_STYLE, &cursor_style);
    if (result != GHOSTTY_SUCCESS) return result;
    self->force_full = true;
    return ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_BLINK, &blink);
}
int ot_resize(OtTerminal* self, uint16_t columns, uint16_t rows, uint32_t width, uint32_t height) {
    self->force_full = true;
    release_capture_text(self);
    ghostty_selection_gesture_reset(self->gesture, self->terminal);
    if (self->render_held) end_hold(self);
    self->cell_width = width; self->cell_height = height;
    GhosttyMouseEncoderSize size = GHOSTTY_INIT_SIZED(GhosttyMouseEncoderSize);
    size.screen_width = columns * width; size.screen_height = rows * height;
    size.cell_width = width; size.cell_height = height;
    ghostty_mouse_encoder_setopt(self->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size);
    return ghostty_terminal_resize(self->terminal, columns, rows, width, height);
}
int ot_scrollbar(OtTerminal* self, uint64_t* total, uint64_t* offset, uint64_t* visible) {
    GhosttyTerminalScrollbar state = {0};
    int result = ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_SCROLLBAR, &state);
    *total = state.total; *offset = state.offset; *visible = state.len;
    return result;
}

void ot_scroll_to(OtTerminal* self, uint64_t row) {
    GhosttyTerminalScrollViewport scroll = {0};
    scroll.tag = GHOSTTY_SCROLL_VIEWPORT_ROW;
    scroll.value.row = (size_t)row;
    ghostty_terminal_scroll_viewport(self->terminal, scroll);
}

void ot_scroll(OtTerminal* self, int delta) {
    if (self->render_held) end_hold(self);
    GhosttyTerminalScrollViewport scroll = {0};
    scroll.tag = delta == 0 ? GHOSTTY_SCROLL_VIEWPORT_BOTTOM : GHOSTTY_SCROLL_VIEWPORT_DELTA;
    scroll.value.delta = delta;
    ghostty_terminal_scroll_viewport(self->terminal, scroll);
}
int ot_frame(OtTerminal* self, OtFrame* frame, OtCell* output, size_t capacity) {
    if (self->render_held && GetTickCount64() - self->hold_started >= 1000) end_hold(self);
    if (!self->render_held) {
        self->animation_delay = ghostty_toiqtty_graphics_tick(self->terminal, GetTickCount64());
        if (ghostty_render_state_update(self->render, self->terminal) != GHOSTTY_SUCCESS) return -1;
        capture_graphics(self);
    }
    uint16_t cols = 0, rows = 0, x = 0, y = 0;
    bool visible = false, has_cursor = false, blinking = false;
    GhosttyRenderStateCursorVisualStyle cursor_style = 0;
    GhosttyColorRgb fg = {215,220,229}, bg = {19,21,25};
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_COLS, &cols);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_ROWS, &rows);
    if (capacity < (size_t)cols * rows) return -2;
    size_t total = (size_t)cols * rows;
    if (self->text_row_count != rows || self->text_columns != cols) {
        release_capture_text(self); self->force_full = true;
        self->text_rows = calloc(rows, sizeof(*self->text_rows));
        self->text_offsets = malloc((size_t)cols * sizeof(*self->text_offsets));
        if (!self->text_rows || !self->text_offsets) { release_capture_text(self); return -3; }
        self->text_row_count = rows; self->text_columns = cols;
    }
    if (rows > self->row_capacity) {
        uint8_t* changed = realloc(self->row_changed, rows);
        if (!changed) return -3;
        self->row_changed = changed; self->row_capacity = rows;
    }
    memset(self->row_changed, 0, rows);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_COLOR_FOREGROUND, &fg);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_COLOR_BACKGROUND, &bg);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE, &visible);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &has_cursor);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &x);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &y);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISUAL_STYLE, &cursor_style);
    GhosttyColorRgb cursor = {217,189,134}, palette[256] = {0};
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_COLOR_CURSOR, &cursor);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_BLINKING, &blinking);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_COLOR_PALETTE, palette);
    bool wide_tail = false;
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_WIDE_TAIL, &wide_tail);
    *frame = (OtFrame){cols, rows, x, y, visible && has_cursor, cursor_style, rgb(fg), rgb(bg), rgb(cursor), blinking, self->render_held, self->cell_width, self->cell_height, self->animation_delay, wide_tail, 0};
    GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FULL;
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
    ghostty_render_state_get(self->render, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &self->iterator);
    size_t index = 0;
    while (ghostty_render_state_row_iterator_next(self->iterator)) {
        int32_t row = 0; bool row_dirty = false;
        ghostty_render_state_row_get(self->iterator, GHOSTTY_RENDER_STATE_ROW_DATA_VIEWPORT_Y, &row);
        ghostty_render_state_row_get(self->iterator, GHOSTTY_RENDER_STATE_ROW_DATA_DIRTY, &row_dirty);
        if (row < 0 || row >= rows) continue;
        if (self->incremental && !self->force_full && dirty != GHOSTTY_RENDER_STATE_DIRTY_FULL && !row_dirty) continue;
        self->row_changed[row] = 1; frame->changed_rows++;
        OtTextRow* text_row = &self->text_rows[row]; text_row->used = 0;
        memset(self->text_offsets, 0xff, (size_t)cols * sizeof(*self->text_offsets));
        index = (size_t)row * cols;
        ghostty_render_state_row_get(self->iterator, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &self->cells);
        while (ghostty_render_state_row_cells_next(self->cells)) {
            size_t cell_index = index++;
            OtCell* cell = &output[cell_index]; memset(cell, 0, sizeof(*cell));
            GhosttyColorRgb cf = fg, cb = bg;
            GhosttyStyle style = GHOSTTY_INIT_SIZED(GhosttyStyle);
            ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &cf);
            ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &cb);
            ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style);
            if (style.inverse) { GhosttyColorRgb swap = cf; cf = cb; cb = swap; }
            cell->foreground = rgb(cf); cell->background = rgb(cb);
            cell->flags = style.bold | (style.italic << 1) | ((style.underline != 0) << 2) |
                          (style.strikethrough << 3) | (style.faint << 4) | (style.invisible << 5) |
                          (style.overline << 7) | (style.blink << 8);
            cell->underline_style = style.underline;
            cell->underline_color = style.underline_color.tag == GHOSTTY_STYLE_COLOR_RGB ? rgb(style.underline_color.value.rgb) :
                style.underline_color.tag == GHOSTTY_STYLE_COLOR_PALETTE ? rgb(palette[style.underline_color.value.palette]) : rgb(cf);
            GhosttyCell raw = {0}; GhosttyCellWide wide = GHOSTTY_CELL_WIDE_NARROW;
            ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw);
            ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide);
            GhosttyCellContentTag content = GHOSTTY_CELL_CONTENT_CODEPOINT;
            ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CONTENT_TAG, &content);
            cell->width = wide == GHOSTTY_CELL_WIDE_WIDE ? 2 : wide == GHOSTTY_CELL_WIDE_NARROW ? 1 : 0;
            // The lowest graphics layer shows through only default backgrounds.
            if (style.bg_color.tag != GHOSTTY_STYLE_COLOR_NONE || style.inverse ||
                content == GHOSTTY_CELL_CONTENT_BG_COLOR_RGB || content == GHOSTTY_CELL_CONTENT_BG_COLOR_PALETTE || cb.r != bg.r || cb.g != bg.g || cb.b != bg.b)
                cell->flags |= 1u << 9;
            bool selected = false;
            ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_SELECTED, &selected);
            cell->flags |= selected << 6;
            GhosttyBuffer buffer = {cell->text, sizeof(cell->text), 0};
            GhosttyResult text_result = ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &buffer);
            if (text_result == GHOSTTY_OUT_OF_SPACE && buffer.len > sizeof(cell->text)) {
                if (buffer.len > UINT32_MAX || !reserve_row_text(text_row, buffer.len)) { self->force_full = true; return -3; }
                buffer.ptr = text_row->bytes + text_row->used; buffer.cap = buffer.len;
                text_result = ghostty_render_state_row_cells_get(self->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &buffer);
                if (text_result == GHOSTTY_SUCCESS) {
                    if (buffer.len > buffer.cap) { self->force_full = true; return -3; }
                    self->text_offsets[cell_index - (size_t)row * cols] = text_row->used;
                    text_row->used += buffer.len;
                }
            }
            if (text_result == GHOSTTY_SUCCESS)
                cell->length = (uint32_t)buffer.len;
            else { cell->text[0] = '?'; cell->length = 1; }
            // Placeholder glyphs are image coordinates, never visible text.
            if (cell->length >= 4 && memcmp(buffer.ptr, "\xf4\x8e\xbb\xae", 4) == 0) cell->length = 0;
        }
        // Arena growth may move bytes. Bind pointers only after the complete
        // dirty row is built. Unchanged rows retain their independent arena.
        trim_row_text(text_row);
        for (size_t column = 0; column < cols; column++)
            if (self->text_offsets[column] != SIZE_MAX)
                output[(size_t)row * cols + column].extended_text = text_row->bytes + self->text_offsets[column];
    }
    ghostty_render_state_clean(self->render);
    self->force_full = false;
    return (int)total;
}
void ot_incremental(OtTerminal* self, int enabled) { self->incremental = enabled != 0; self->force_full = true; }
int ot_row_changed(OtTerminal* self, uint32_t row) { return row < self->row_capacity && self->row_changed[row]; }
int ot_graphics(OtTerminal* self, const OtGraphic** graphics) { *graphics = self->graphics; return (int)self->graphics_count; }
uint64_t ot_graphics_cache_bytes(OtTerminal* self) {
    uint64_t bytes = 0;
    for (OtImage* image = self->images; image; image = image->next) bytes += image->length;
    return bytes;
}
int ot_hyperlink(OtTerminal* self, uint16_t column, uint16_t row, uint8_t* output, size_t capacity) {
    GhosttyPoint point = {0}; GhosttyGridRef ref = {0}; size_t length = 0;
    point.tag = GHOSTTY_POINT_TAG_VIEWPORT; point.value.coordinate = (GhosttyPointCoordinate){column, row};
    if (ghostty_terminal_grid_ref(self->terminal, point, &ref) != GHOSTTY_SUCCESS) return 0;
    GhosttyResult result = ghostty_grid_ref_hyperlink_uri(&ref, output, capacity, &length);
    return (result == GHOSTTY_SUCCESS || result == GHOSTTY_OUT_OF_SPACE) && length <= INT32_MAX ? (int)length : 0;
}
static GhosttyKey map_key(uint32_t vk) {
    if (vk >= 'A' && vk <= 'Z') return GHOSTTY_KEY_A + vk - 'A';
    if (vk >= '0' && vk <= '9') return GHOSTTY_KEY_DIGIT_0 + vk - '0';
    if (vk >= 112 && vk <= 135) return GHOSTTY_KEY_F1 + vk - 112;
    if (vk >= 96 && vk <= 105) return GHOSTTY_KEY_NUMPAD_0 + vk - 96;
    switch (vk) {
        case 8: return GHOSTTY_KEY_BACKSPACE; case 9: return GHOSTTY_KEY_TAB;
        case 13: return GHOSTTY_KEY_ENTER; case 27: return GHOSTTY_KEY_ESCAPE;
        case 32: return GHOSTTY_KEY_SPACE; case 33: return GHOSTTY_KEY_PAGE_UP;
        case 34: return GHOSTTY_KEY_PAGE_DOWN; case 35: return GHOSTTY_KEY_END;
        case 36: return GHOSTTY_KEY_HOME; case 37: return GHOSTTY_KEY_ARROW_LEFT;
        case 38: return GHOSTTY_KEY_ARROW_UP; case 39: return GHOSTTY_KEY_ARROW_RIGHT;
        case 40: return GHOSTTY_KEY_ARROW_DOWN; case 45: return GHOSTTY_KEY_INSERT;
        case 46: return GHOSTTY_KEY_DELETE; case 186: return GHOSTTY_KEY_SEMICOLON;
        case 12: return GHOSTTY_KEY_NUMPAD_CLEAR; case 19: return GHOSTTY_KEY_PAUSE;
        case 20: return GHOSTTY_KEY_CAPS_LOCK; case 44: return GHOSTTY_KEY_PRINT_SCREEN;
        case 106: return GHOSTTY_KEY_NUMPAD_MULTIPLY; case 107: return GHOSTTY_KEY_NUMPAD_ADD;
        case 108: return GHOSTTY_KEY_NUMPAD_SEPARATOR; case 109: return GHOSTTY_KEY_NUMPAD_SUBTRACT;
        case 110: return GHOSTTY_KEY_NUMPAD_DECIMAL; case 111: return GHOSTTY_KEY_NUMPAD_DIVIDE;
        case 144: return GHOSTTY_KEY_NUM_LOCK; case 145: return GHOSTTY_KEY_SCROLL_LOCK;
        case 187: return GHOSTTY_KEY_EQUAL; case 188: return GHOSTTY_KEY_COMMA;
        case 189: return GHOSTTY_KEY_MINUS; case 190: return GHOSTTY_KEY_PERIOD;
        case 191: return GHOSTTY_KEY_SLASH; case 192: return GHOSTTY_KEY_BACKQUOTE;
        case 219: return GHOSTTY_KEY_BRACKET_LEFT; case 220: return GHOSTTY_KEY_BACKSLASH;
        case 221: return GHOSTTY_KEY_BRACKET_RIGHT; case 222: return GHOSTTY_KEY_QUOTE;
        case 160: case 16: return GHOSTTY_KEY_SHIFT_LEFT; case 161: return GHOSTTY_KEY_SHIFT_RIGHT;
        case 162: case 17: return GHOSTTY_KEY_CONTROL_LEFT; case 163: return GHOSTTY_KEY_CONTROL_RIGHT;
        case 164: case 18: return GHOSTTY_KEY_ALT_LEFT; case 165: return GHOSTTY_KEY_ALT_RIGHT;
        case 91: return GHOSTTY_KEY_META_LEFT; case 92: return GHOSTTY_KEY_META_RIGHT;
        case 0x1000d: return GHOSTTY_KEY_NUMPAD_ENTER;
        default: return GHOSTTY_KEY_UNIDENTIFIED;
    }
}
int ot_key(OtTerminal* self, uint32_t vk, uint32_t mods, int action, uint32_t unshifted, uint32_t consumed, const char* text, size_t length, char* output, size_t capacity) {
    ghostty_key_encoder_setopt_from_terminal(self->encoder, self->terminal);
    ghostty_key_event_set_action(self->event, action == 0 ? GHOSTTY_KEY_ACTION_RELEASE : action == 2 ? GHOSTTY_KEY_ACTION_REPEAT : GHOSTTY_KEY_ACTION_PRESS);
    ghostty_key_event_set_key(self->event, map_key(vk));
    ghostty_key_event_set_mods(self->event, (GhosttyMods)mods);
    ghostty_key_event_set_consumed_mods(self->event, (GhosttyMods)consumed);
    ghostty_key_event_set_utf8(self->event, text, length);
    ghostty_key_event_set_unshifted_codepoint(self->event, unshifted);
    size_t written = 0;
    if (ghostty_key_encoder_encode(self->encoder, self->event, output, capacity, &written) != GHOSTTY_SUCCESS) return -1;
    return (int)written;
}
int ot_paste(OtTerminal* self, char* text, size_t length, char* output, size_t capacity) {
    GhosttyTerminalModeConfig mode = {GHOSTTY_MODE_BRACKETED_PASTE, false};
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode);
    size_t written = 0;
    if (ghostty_paste_encode(text, length, mode.value, output, capacity, &written) != GHOSTTY_SUCCESS) return -1;
    return (int)written;
}
typedef struct { const uint8_t* text; size_t length; } OtPasteText;
static bool paste_text(void* context, GhosttyString mime, GhosttyWriter writer) {
    (void)mime; OtPasteText* text = context;
    return text->length == 0 || writer.write(writer.userdata, text->text, text->length);
}
int ot_clipboard_paste(OtTerminal* self, const uint8_t* text, size_t length, int clipboard) {
    OtPasteText source = {text, length}; GhosttyString mime = {(const uint8_t*)"text/plain", 10};
    GhosttyPaste paste = GHOSTTY_INIT_SIZED(GhosttyPaste);
    paste.source = clipboard ? GHOSTTY_PASTE_SOURCE_CLIPBOARD : GHOSTTY_PASTE_SOURCE_TEXT;
    paste.location = GHOSTTY_CLIPBOARD_LOCATION_STANDARD; paste.mimes = &mime; paste.mimes_len = 1;
    paste.reader = (GhosttyMimeReader){paste_text, &source};
    // A paste is an explicit user action; Ghostty still strips unsafe control bytes.
    paste.allow_unsafe = true;
    return ghostty_terminal_paste(self->terminal, &paste, NULL);
}
typedef struct { OtMimeReader reader; void* context; } OtPasteReader;
static bool paste_mime(void* context, GhosttyString mime, GhosttyWriter writer) {
    OtPasteReader* source = context;
    return source->reader(source->context, (OtString){mime.ptr, mime.len}, (OtWriter){writer.write, writer.userdata});
}
int ot_clipboard_paste_mimes(OtTerminal* self, const OtString* mimes, size_t count, OtMimeReader reader, void* context) {
    if (!reader || count > 256) return -1;
    OtPasteReader source = {reader, context};
    GhosttyString types[256];
    for (size_t i = 0; i < count; i++) types[i] = (GhosttyString){mimes[i].ptr, mimes[i].len};
    GhosttyPaste paste = GHOSTTY_INIT_SIZED(GhosttyPaste);
    paste.source = GHOSTTY_PASTE_SOURCE_CLIPBOARD;
    paste.location = GHOSTTY_CLIPBOARD_LOCATION_STANDARD;
    paste.mimes = types; paste.mimes_len = count;
    paste.reader = (GhosttyMimeReader){paste_mime, &source}; paste.allow_unsafe = true;
    return ghostty_terminal_paste(self->terminal, &paste, NULL);
}
int ot_drop(OtTerminal* self, int action, int x, int y, uint32_t operations, const OtClipboardContent* items, size_t count) {
    return ghostty_toiqtty_dnd(self->terminal, action, x, y, self->cell_width, self->cell_height, operations, items, count);
}
int ot_metadata(OtTerminal* self, int kind, uint8_t* output, size_t capacity) {
    GhosttyString value = {0};
    if (ghostty_terminal_get(self->terminal, kind == 0 ? GHOSTTY_TERMINAL_DATA_TITLE : GHOSTTY_TERMINAL_DATA_PWD, &value) != GHOSTTY_SUCCESS) return 0;
    size_t length = value.len < capacity ? value.len : capacity;
    if (length) memcpy(output, value.ptr, length);
    return (int)length;
}
int ot_mouse_tracking(OtTerminal* self) {
    bool tracking = false;
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking);
    return tracking ? 1 : 0;
}
int ot_mouse(OtTerminal* self, int action, int button, uint32_t mods, float x, float y, char* output, size_t capacity) {
    bool tracking = false;
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking);
    if (!tracking) return -1;
    ghostty_mouse_encoder_setopt_from_terminal(self->mouse_encoder, self->terminal);
    ghostty_mouse_event_set_action(self->mouse_event, (GhosttyMouseAction)action);
    if (button) ghostty_mouse_event_set_button(self->mouse_event, (GhosttyMouseButton)button);
    else ghostty_mouse_event_clear_button(self->mouse_event);
    bool pressed = action != GHOSTTY_MOUSE_ACTION_RELEASE && button != 0;
    ghostty_mouse_encoder_setopt(self->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED, &pressed);
    ghostty_mouse_event_set_mods(self->mouse_event, (GhosttyMods)mods);
    ghostty_mouse_event_set_position(self->mouse_event, (GhosttyMousePosition){x,y});
    size_t written = 0;
    if (ghostty_mouse_encoder_encode(self->mouse_encoder, self->mouse_event, output, capacity, &written) != GHOSTTY_SUCCESS) return 0;
    return (int)written;
}
void ot_focus(OtTerminal* self, int focused) {
    GhosttyTerminalModeConfig mode = {GHOSTTY_MODE_FOCUS_EVENT, false};
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode);
    if (!mode.value) return;
    char buffer[16]; size_t written = 0;
    if (ghostty_focus_encode(focused ? GHOSTTY_FOCUS_GAINED : GHOSTTY_FOCUS_LOST, buffer, sizeof(buffer), &written) == GHOSTTY_SUCCESS)
        write_pty(self->terminal, self, (const uint8_t*)buffer, written);
}
void ot_select(OtTerminal* self, int start, int end) {
    if (start < 0 || end < 0) { ghostty_selection_gesture_reset(self->gesture, self->terminal); ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL); return; }
    uint16_t columns = 0;
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_COLS, &columns);
    if (!columns) return;
    GhosttyPoint first = {0}, last = {0};
    first.tag = last.tag = GHOSTTY_POINT_TAG_VIEWPORT;
    first.value.coordinate = (GhosttyPointCoordinate){(uint16_t)(start % columns), (uint32_t)(start / columns)};
    last.value.coordinate = (GhosttyPointCoordinate){(uint16_t)(end % columns), (uint32_t)(end / columns)};
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_terminal_grid_ref(self->terminal, first, &selection.start) != GHOSTTY_SUCCESS ||
        ghostty_terminal_grid_ref(self->terminal, last, &selection.end) != GHOSTTY_SUCCESS) return;
    ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
}
int ot_gesture(OtTerminal* self, int action, float x, float y, int rectangle) {
    if (action < 0 || action > 3) return -1;
    uint16_t columns = 0, rows = 0;
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_COLS, &columns);
    ghostty_terminal_get(self->terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows);
    if (!columns || !rows || !self->cell_width || !self->cell_height) return -1;
    GhosttySelectionGestureEvent event = self->gesture_events[action];
    GhosttyPoint point = {0}; point.tag = GHOSTTY_POINT_TAG_VIEWPORT;
    int col = (int)(x / self->cell_width), row = (int)(y / self->cell_height);
    point.value.coordinate = (GhosttyPointCoordinate){col < 0 ? 0 : col >= columns ? columns - 1 : col, row < 0 ? 0 : row >= rows ? rows - 1 : row};
    GhosttyGridRef ref = {0};
    if (action != 3 && ghostty_terminal_grid_ref(self->terminal, point, &ref) == GHOSTTY_SUCCESS)
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &ref);
    GhosttySurfacePosition position = {x, y};
    GhosttySelectionGestureGeometry geometry = {columns, self->cell_width, 0, rows * self->cell_height};
    bool rect = rectangle != 0;
    if (action == 0 || action == 2 || action == 3) ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &position);
    if (action == 0) {
        uint64_t now = GetTickCount64() * 1000000, interval = (uint64_t)GetDoubleClickTime() * 1000000;
        double distance = GetSystemMetrics(SM_CXDOUBLECLK);
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_TIME_NS, &now);
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_INTERVAL_NS, &interval);
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_DISTANCE, &distance);
    }
    if (action >= 2) {
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_GEOMETRY, &geometry);
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_RECTANGLE, &rect);
    }
    if (action == 3) {
        // Scrolling first preserves the gesture's tracked anchor in scrollback.
        GhosttySelectionGestureAutoscroll direction = 0;
        ghostty_selection_gesture_get(self->gesture, self->terminal, GHOSTTY_SELECTION_GESTURE_DATA_AUTOSCROLL, &direction);
        if (direction == 0) return 0;
        ot_scroll(self, direction == GHOSTTY_SELECTION_GESTURE_AUTOSCROLL_UP ? -1 : 1);
        ghostty_selection_gesture_event_set(event, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_VIEWPORT, &point.value.coordinate);
    }
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    GhosttyResult result = ghostty_selection_gesture_event(self->gesture, self->terminal, event, &selection);
    if (result == GHOSTTY_SUCCESS) ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
    else if (result == GHOSTTY_NO_VALUE && action == 0) ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
    return result == GHOSTTY_SUCCESS ? 1 : result == GHOSTTY_NO_VALUE ? 0 : -1;
}
int ot_copy(OtTerminal* self, uint8_t* output, size_t capacity) {
    GhosttyTerminalSelectionFormatOptions options = GHOSTTY_INIT_SIZED(GhosttyTerminalSelectionFormatOptions);
    options.emit = GHOSTTY_FORMATTER_FORMAT_PLAIN; options.unwrap = true; options.trim = true;
    size_t written = 0;
    GhosttyResult result = ghostty_terminal_selection_format_buf(self->terminal, options, output, capacity, &written);
    if (result != GHOSTTY_SUCCESS && result != GHOSTTY_OUT_OF_SPACE) return 0;
    return written > INT32_MAX ? 0 : (int)written;
}
int ot_find(OtTerminal* self, const uint8_t* text, size_t length, int direction, size_t* count, size_t* index) {
    GhosttyString needle = {text, length};
    *count = *index = 0;
    if (ghostty_search_set(self->search, GHOSTTY_SEARCH_OPT_NEEDLE, &needle) != GHOSTTY_SUCCESS ||
        ghostty_search_run(self->search) != GHOSTTY_SUCCESS) return -1;
    if (direction) ghostty_search_set(self->search, direction > 0 ? GHOSTTY_SEARCH_OPT_SELECT_NEXT : GHOSTTY_SEARCH_OPT_SELECT_PREV, NULL);
    else {
        size_t selected;
        if (ghostty_search_get(self->search, GHOSTTY_SEARCH_DATA_SELECTED_INDEX, &selected) != GHOSTTY_SUCCESS)
            ghostty_search_set(self->search, GHOSTTY_SEARCH_OPT_SELECT_NEXT, NULL);
    }
    ghostty_search_get(self->search, GHOSTTY_SEARCH_DATA_TOTAL_MATCHES, count);
    ghostty_search_get(self->search, GHOSTTY_SEARCH_DATA_SELECTED_INDEX, index);
    GhosttySelection selection = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_search_get(self->search, GHOSTTY_SEARCH_DATA_SELECTED_MATCH, &selection) == GHOSTTY_SUCCESS)
        ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
    else ghostty_terminal_set(self->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
    return 0;
}

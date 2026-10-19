#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define OTR_API __declspec(dllexport)
typedef struct OtrRenderer OtrRenderer;
typedef struct OtrTexture OtrTexture;
typedef struct { float x, y, width, height; } OtrRect;
typedef struct {
    uint64_t frames, draw_calls, vertices, atlas_hits, atlas_misses, atlas_evictions, uploaded_bytes;
    uint64_t texture_bytes, atlas_bytes;
    uint32_t atlas_entries, feature_level;
    int32_t device_removed_reason;
    uint32_t debug_layer;
    uint64_t debug_errors, debug_warnings;
    uint64_t shape_cache_bytes;
    uint64_t vertex_buffer_bytes;
    uint32_t backend_api, submission_slots;
    uint64_t submissions;
    uint64_t interop_warnings;
} OtrStats;
// Opt-in CPU profiling, separate from the stable rendering statistics ABI.
typedef struct {
    uint64_t frequency, lists, resets, fence_waits, fence_wait_ticks, reset_ticks, execute_ticks;
    uint64_t upload_creates, upload_create_ticks, raster_transfers, raster_transfer_ticks, raster_draw_ticks, present_ticks;
    uint64_t pipeline_ticks, device_private_before, device_private_created, device_private_bridge, device_private_d2d;
    uint64_t renderer_private_before, renderer_private_after, descriptor_heaps, texture_bytes, upload_bytes;
} OtrProfile;
typedef struct {
    uint64_t submission_arena_bytes, pin_requests, unique_pins, live_pins;
    uint64_t shape_arena_bytes, device_pipeline_builds, descriptor_pages, upload_bytes;
} OtrLifetimeStats;
// All functions run on the owning XAML thread. Borrowed COM inputs are AddRef'd
// only when retained. HRESULTs never cross the C ABI as C++ exceptions.
OTR_API int32_t otr_create(void* canvas_device, OtrRenderer** result);
OTR_API void otr_destroy(OtrRenderer* renderer);
OTR_API void otr_shutdown_devices(void);
OTR_API int32_t otr_resize(OtrRenderer*, uint32_t width, uint32_t height, float dpi, int32_t transparent);
OTR_API int32_t otr_swapchain(OtrRenderer*, void** result);
OTR_API int32_t otr_present(OtrRenderer*);
OTR_API int32_t otr_acquire_frame(OtrRenderer*);
OTR_API int32_t otr_begin_frame(OtrRenderer*);
OTR_API int32_t otr_device(OtrRenderer*, void** result);
OTR_API int32_t otr_present_surface(OtrRenderer*, void* drawing_surface);
OTR_API int32_t otr_resize_surface(void* drawing_surface, uint32_t width, uint32_t height);
OTR_API int32_t otr_texture_create(OtrRenderer*, uint32_t width, uint32_t height, float dpi, const uint8_t* pixels, int32_t target, OtrTexture** result);
OTR_API int32_t otr_texture_from_bitmap(OtrRenderer*, void* bitmap, float dpi, OtrTexture** result);
OTR_API void otr_texture_destroy(OtrTexture*);
OTR_API int32_t otr_begin(OtrRenderer*, OtrTexture* target);
OTR_API int32_t otr_end(OtrRenderer*);
OTR_API int32_t otr_clear(OtrRenderer*, uint32_t argb);
OTR_API int32_t otr_clip(OtrRenderer*, OtrRect);
OTR_API int32_t otr_fill(OtrRenderer*, OtrRect, uint32_t argb, int32_t copy);
OTR_API int32_t otr_triangle(OtrRenderer*, float x0,float y0,float x1,float y1,float x2,float y2,uint32_t argb);
OTR_API int32_t otr_powerline(OtrRenderer*, OtrRect, uint32_t argb, int32_t shape);
OTR_API int32_t otr_line(OtrRenderer*, float x0,float y0,float x1,float y1,float width,uint32_t argb);
OTR_API int32_t otr_image(OtrRenderer*, OtrTexture*, OtrRect destination, OtrRect source, float opacity, int32_t linear);
OTR_API int32_t otr_text(OtrRenderer*, void* layout, float x, float y, uint32_t argb);
OTR_API int32_t otr_forget_text(OtrRenderer*, void* layout);
OTR_API void otr_stats(OtrRenderer*, OtrStats*);
OTR_API void otr_profile(OtrRenderer*, OtrProfile*);
OTR_API void otr_lifetime_stats(OtrRenderer*, OtrLifetimeStats*);
OTR_API void otr_text_mode_for_check(OtrRenderer*, int32_t layout_mode);
OTR_API uint32_t otr_debug_messages(OtrRenderer*, uint8_t* destination, uint32_t capacity);
OTR_API int32_t otr_read_pixels_for_check(OtrRenderer*, uint8_t* destination, uint64_t capacity);
// A fresh descriptor for the same immutable sampled image; no pixel allocation.
OTR_API int32_t otr_texture_alias_for_check(OtrRenderer*, OtrTexture*, OtrTexture**);
#ifdef __cplusplus
}
#endif

// Windows' PNG codec supplies Ghostty's optional decoder; no second VT parser.
#define COBJMACROS
#include <windows.h>
#include <wincodec.h>
#include <ghostty/vt.h>

static bool decode_png(void* context, const GhosttyAllocator* allocator,
                       const uint8_t* data, size_t length, GhosttySysImage* out) {
    (void)context;
    if (!length || length > UINT32_MAX) return false;
    HRESULT apartment = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE) return false;
    IWICImagingFactory* factory = NULL;
    IWICStream* stream = NULL;
    IWICBitmapDecoder* decoder = NULL;
    IWICBitmapFrameDecode* frame = NULL;
    IWICFormatConverter* converter = NULL;
    uint8_t* pixels = NULL;
    UINT width = 0, height = 0;
    size_t bytes = 0;
    bool ok = false;
    if (FAILED(CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
                                &IID_IWICImagingFactory, (void**)&factory))) goto done;
    if (FAILED(IWICImagingFactory_CreateStream(factory, &stream)) ||
        FAILED(IWICStream_InitializeFromMemory(stream, (BYTE*)data, (DWORD)length)) ||
        FAILED(IWICImagingFactory_CreateDecoderFromStream(factory, (IStream*)stream, NULL,
                WICDecodeMetadataCacheOnDemand, &decoder)) ||
        FAILED(IWICBitmapDecoder_GetFrame(decoder, 0, &frame)) ||
        FAILED(IWICBitmapFrameDecode_GetSize(frame, &width, &height))) goto done;
    // Bound decoded size before allocating or asking WIC to decompress pixels.
    bytes = (size_t)width * height * 4;
    if (!width || !height || width > 16384 || height > 16384 || bytes > 64u * 1024 * 1024) goto done;
    if (FAILED(IWICImagingFactory_CreateFormatConverter(factory, &converter)) ||
        FAILED(IWICFormatConverter_Initialize(converter, (IWICBitmapSource*)frame,
                &GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom))) goto done;
    pixels = ghostty_alloc(allocator, bytes);
    if (!pixels || FAILED(IWICFormatConverter_CopyPixels(converter, NULL, width * 4, (UINT)bytes, pixels))) goto done;
    *out = (GhosttySysImage){width, height, pixels, bytes};
    pixels = NULL; ok = true;
done:
    if (pixels) ghostty_free(allocator, pixels, bytes);
    if (converter) IWICFormatConverter_Release(converter);
    if (frame) IWICBitmapFrameDecode_Release(frame);
    if (decoder) IWICBitmapDecoder_Release(decoder);
    if (stream) IWICStream_Release(stream);
    if (factory) IWICImagingFactory_Release(factory);
    if (SUCCEEDED(apartment)) CoUninitialize();
    return ok;
}
static BOOL CALLBACK initialize(PINIT_ONCE once, PVOID parameter, PVOID* context) {
    (void)once; (void)parameter; (void)context;
    return ghostty_sys_set(GHOSTTY_SYS_OPT_DECODE_PNG, (const void*)decode_png) == GHOSTTY_SUCCESS;
}
bool ot_initialize_codecs(void) {
    static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
    return InitOnceExecuteOnce(&once, initialize, NULL, NULL) != 0;
}

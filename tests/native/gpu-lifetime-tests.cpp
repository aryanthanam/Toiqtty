#include <windows.h>
#include <d3d11.h>
#include <d2d1_3.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <cstdio>
#include <memory>
#include <stdexcept>
#include <vector>
#include "renderer.h"
using Microsoft::WRL::ComPtr;

namespace {
int checks=0;
void require(bool condition,const char* message){if(!condition)throw std::runtime_error(message);checks++;}
void check(HRESULT result){if(FAILED(result)){std::printf("HRESULT: %08lx\n",static_cast<unsigned long>(result));throw std::runtime_error("Native GPU call failed");}}
// The same public Win2D resource ABI consumed by the renderer, backed here by
// real Direct2D/DirectWrite objects. No XAML window or composition engine loads.
struct __declspec(uuid("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A")) ResourceAbi : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetNativeResource(IUnknown*,float,REFIID,void**)=0;
};
class Resource final : public ResourceAbi {
    ComPtr<IUnknown> object;
    ULONG refs=1;
public:
    explicit Resource(IUnknown* value):object(value){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if(!result)return E_POINTER;*result=nullptr;
        if(id==__uuidof(IUnknown) || id==__uuidof(ResourceAbi)){*result=static_cast<ResourceAbi*>(this);AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{return --refs;}
    HRESULT STDMETHODCALLTYPE GetNativeResource(IUnknown*,float,REFIID id,void** result) override{return object->QueryInterface(id,result);}
};
void exercise(bool software) {
    ComPtr<ID3D11Device> provider;
    check(D3D11CreateDevice(nullptr,software?D3D_DRIVER_TYPE_WARP:D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&provider,nullptr,nullptr));
    ComPtr<IDXGIDevice> dxgi;check(provider.As(&dxgi));
    ComPtr<ID2D1Factory3> factory;D2D1_FACTORY_OPTIONS options{};
    check(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,__uuidof(ID2D1Factory3),&options,reinterpret_cast<void**>(factory.GetAddressOf())));
    ComPtr<ID2D1Device2> d2d;check(factory->CreateDevice(dxgi.Get(),&d2d));Resource canvas(d2d.Get());
    OtrRenderer* created=nullptr;check(otr_create(&canvas,&created));
    std::unique_ptr<OtrRenderer,decltype(&otr_destroy)> renderer(created,otr_destroy);auto r=renderer.get();
    check(otr_resize(r,64,64,96,0));
    OtrLifetimeStats initial{};otr_lifetime_stats(r,&initial);
    require(initial.device_pipeline_builds==1,"Shared pipelines initialize once per device");

    uint8_t green[]={0,255,0,255};OtrTexture* image=nullptr;
    check(otr_texture_create(r,1,1,96,green,0,&image));
    OtrLifetimeStats before{};otr_lifetime_stats(r,&before);
    check(otr_begin_frame(r));check(otr_begin(r,nullptr));check(otr_clear(r,0xff0000ff));
    for(int i=0;i<15000;i++){
        check(otr_image(r,image,{0,0,32,64},{0,0,1,1},1,0));
        check(otr_fill(r,{32,0,32,64},0xffff0000,0));
    }
    // Dispose the source while the last image batch is still pending.
    check(otr_image(r,image,{0,0,32,64},{0,0,1,1},1,0));otr_texture_destroy(image);image=nullptr;
    check(otr_end(r));
    OtrLifetimeStats recorded{};otr_lifetime_stats(r,&recorded);
    require(recorded.pin_requests-before.pin_requests>=60000 && recorded.unique_pins-before.unique_pins<=8,"Submission pins are deduplicated across upload rollover");
    require(recorded.upload_bytes<=12*1024*1024,"Upload arenas remain bounded");
    std::vector<uint8_t> pixels(96*96*4);check(otr_read_pixels_for_check(r,pixels.data(),pixels.size()));
    require(pixels[0]==0 && pixels[1]==255 && pixels[2]==0 && pixels[63*4]==0 && pixels[63*4+2]==255,"Owner disposal preserves actual GPU pixels");
    check(otr_present(r));check(otr_read_pixels_for_check(r,pixels.data(),pixels.size()));
    OtrStats stats{};otr_stats(r,&stats);OtrLifetimeStats retired{};otr_lifetime_stats(r,&retired);
    require(retired.live_pins==0 && retired.submission_arena_bytes<=3*16384,"Fence retirement releases pins and recycles arena pages");

    ComPtr<IDWriteFactory> write;check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(write.GetAddressOf())));
    ComPtr<IDWriteTextFormat> format;check(write->CreateTextFormat(L"Consolas",nullptr,DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,14,L"en-us",&format));
    ComPtr<IDWriteTextLayout> layout;check(write->CreateTextLayout(L"arena glyphs",12,format.Get(),256,32,&layout));Resource text(layout.Get());
    check(otr_begin(r,nullptr));check(otr_text(r,&text,0,0,0xffffffff));check(otr_end(r));otr_stats(r,&stats);
    require(stats.shape_cache_bytes>0,"DirectWrite glyph arrays populate an entry-owned arena");
    check(otr_forget_text(r,&text));otr_stats(r,&stats);require(stats.shape_cache_bytes==0,"Layout retirement releases the complete shape arena");

    void* identity=nullptr;check(otr_swapchain(r,&identity));ComPtr<IUnknown> original;original.Attach(static_cast<IUnknown*>(identity));
    bool stable=true;
    for(int i=0;i<30;i++){
        auto side=static_cast<uint32_t>(i%2?64:96);check(otr_resize(r,side,side,96.f+24.f*(i%5),i%2));
        void* next=nullptr;check(otr_swapchain(r,&next));stable&=next==identity;static_cast<IUnknown*>(next)->Release();
        check(otr_begin(r,nullptr));check(otr_clear(r,0xff00ff00));check(otr_end(r));check(otr_read_pixels_for_check(r,pixels.data(),pixels.size()));
        require(pixels[0]==0 && pixels[1]==255 && pixels[2]==0 && pixels[3]==255,"Resize preserves native pixels");
    }
    require(stable,"The swapchain has pane lifetime across alpha/DPI/size changes");
    // Teardown must abandon a recorded list before releasing its resources.
    check(otr_begin_frame(r));check(otr_begin(r,nullptr));check(otr_fill(r,{0,0,8,8},0xffff0000,0));check(otr_end(r));
    otr_stats(r,&stats);require(stats.debug_errors==0 && stats.debug_warnings==stats.interop_warnings,"D3D12 debug validation has no errors or unexpected warnings");
    std::printf("PASS: %s GPU lifetime scope\n",software?"WARP":"Hardware");
}
}
int main() {
    try{
        check(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED));SetEnvironmentVariableW(L"OROTERM_DIRECTX_DEBUG",L"1");
        exercise(false);exercise(true);otr_shutdown_devices();CoUninitialize();
        std::printf("%d native GPU lifetime checks passed.\n",checks);return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"FAIL: %s\n",error.what());otr_shutdown_devices();return 1;}
}

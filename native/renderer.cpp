#include "renderer.h"
#include <windows.h>
#include <d3d11_4.h>
#include <d3d11sdklayers.h>
#include <dxgi1_6.h>
#include <d2d1_3.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>
#include <span>
#include "renderer-lifetime.h"
#include "renderer-vs.h"
#include "renderer-ps.h"
using Microsoft::WRL::ComPtr;

namespace {
struct Failure { HRESULT hr; };
void check(HRESULT hr) { if (FAILED(hr)) throw Failure{hr}; }
struct __declspec(uuid("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A")) CanvasResource : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetNativeResource(IUnknown*, float, REFIID, void**) = 0;
};
struct __declspec(uuid("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578")) DrawingSurfaceInterop : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE BeginDraw(const RECT*,REFIID,void**,POINT*)=0;
    virtual HRESULT STDMETHODCALLTYPE EndDraw()=0;
    virtual HRESULT STDMETHODCALLTYPE Resize(SIZE)=0;
};
template<class T> ComPtr<T> unwrap(void* object) {
    if (!object) throw Failure{E_POINTER};
    ComPtr<CanvasResource> wrapper;
    check(static_cast<IUnknown*>(object)->QueryInterface(IID_PPV_ARGS(&wrapper)));
    ComPtr<T> native;
    check(wrapper->GetNativeResource(nullptr, 0, IID_PPV_ARGS(&native)));
    return native;
}
struct Accounting { std::atomic<uint64_t> textures{0}; };
struct TextureAllocation {
    std::shared_ptr<Accounting> accounting;
    uint64_t bytes;
    TextureAllocation(std::shared_ptr<Accounting> owner,uint64_t size):accounting(std::move(owner)),bytes(size){accounting->textures.fetch_add(bytes,std::memory_order_relaxed);}
    ~TextureAllocation(){accounting->textures.fetch_sub(bytes,std::memory_order_relaxed);}
};
struct D2DLock {
    ID2D1Multithread* gate;
    explicit D2DLock(ID2D1Multithread* value):gate(value){gate->Enter();}
    ~D2DLock(){gate->Leave();}
};
struct D3DLock {
    ID3D11Multithread* gate;
    explicit D3DLock(ID3D11Multithread* value):gate(value){gate->Enter();}
    ~D3DLock(){gate->Leave();}
};
struct Vertex { float x,y,u,v,r,g,b,a,shape; };
struct Color { float r,g,b,a; };
Color color(uint32_t argb) {
    float a = static_cast<float>(argb >> 24) / 255;
    return {static_cast<float>((argb >> 16)&255)/255*a, static_cast<float>((argb >> 8)&255)/255*a, static_cast<float>(argb&255)/255*a, a};
}
D2D1_COLOR_F straight(uint32_t argb) {
    return D2D1::ColorF(static_cast<float>((argb>>16)&255)/255,static_cast<float>((argb>>8)&255)/255,static_cast<float>(argb&255)/255,static_cast<float>(argb>>24)/255);
}
constexpr uint32_t AtlasSide = 2048, AtlasPages = 2, MaxAtlasEntries = 2048;
constexpr size_t MaxBatchVertices = 98304;
struct AtlasKey {
    IUnknown* resource; uint32_t color; float dpi;
    uint32_t glyph=0; float em=0,phaseX=0,phaseY=0;
    bool operator==(const AtlasKey& k) const { return resource==k.resource && color==k.color && dpi==k.dpi && glyph==k.glyph && em==k.em && phaseX==k.phaseX && phaseY==k.phaseY; }
};
struct AtlasHash {
    size_t operator()(const AtlasKey& k) const {
        uint32_t dpi; std::memcpy(&dpi,&k.dpi,sizeof(dpi));
        auto hash=std::hash<float>{};
        return (reinterpret_cast<uintptr_t>(k.resource)>>4) ^ (static_cast<size_t>(k.color)<<1) ^ dpi ^ (k.glyph<<8) ^ hash(k.em) ^ (hash(k.phaseX)<<1) ^ (hash(k.phaseY)<<2);
    }
};
struct GlyphRun {
    ComPtr<IDWriteFontFace> face;
    float x,y,em;
    std::span<UINT16> glyphs;
    std::span<float> advances;
    std::span<DWRITE_GLYPH_OFFSET> offsets;
};
struct Shape {
    alignas(std::max_align_t) std::byte local[128];
    CacheMemory memory;
    std::pmr::monotonic_buffer_resource arena{local,sizeof(local),&memory};
    ComPtr<IDWriteTextLayout> layout;
    std::pmr::vector<GlyphRun> runs{&arena};
    bool fallback=false;
    size_t bytes() const {return sizeof(Shape)+memory.bytes();}
};
class Collector final : public IDWriteTextRenderer {
    Shape& shape;
    ULONG refs=1;
public:
    explicit Collector(Shape& value):shape(value) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** result) override {
        if(!result)return E_POINTER; *result=nullptr;
        if(iid==__uuidof(IUnknown) || iid==__uuidof(IDWriteTextRenderer) || iid==__uuidof(IDWritePixelSnapping)) {*result=static_cast<IDWriteTextRenderer*>(this);AddRef();return S_OK;} return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {return --refs;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* value) override {*value=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* value) override {*value={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,float* value) override {*value=1;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,float x,float y,DWRITE_MEASURING_MODE,const DWRITE_GLYPH_RUN* run,const DWRITE_GLYPH_RUN_DESCRIPTION*,IUnknown*) override {
        try {
            ComPtr<IDWriteFontFace2> colorFace;
            if(run->isSideways || (run->bidiLevel&1) || (SUCCEEDED(run->fontFace->QueryInterface(IID_PPV_ARGS(&colorFace))) && colorFace->IsColorFont())) {shape.fallback=true;return S_OK;}
            // Italic outlines can overlap adjacent cells. Rasterizing the whole
            // shaped run preserves DirectWrite's joint coverage at those joins.
            ComPtr<IDWriteFontFace3> styledFace;
            if(SUCCEEDED(run->fontFace->QueryInterface(IID_PPV_ARGS(&styledFace))) && styledFace->GetStyle()!=DWRITE_FONT_STYLE_NORMAL) {shape.fallback=true;return S_OK;}
            if(!run->glyphCount)return S_OK;
            if(run->glyphAdvances)for(UINT32 i=0;i<run->glyphCount;i++)if(run->glyphAdvances[i]<=0) {
                DWRITE_GLYPH_METRICS metrics{};check(run->fontFace->GetDesignGlyphMetrics(run->glyphIndices+i,1,&metrics));
                if(static_cast<int64_t>(metrics.advanceWidth)-metrics.leftSideBearing-metrics.rightSideBearing>0 && static_cast<int64_t>(metrics.advanceHeight)-metrics.topSideBearing-metrics.bottomSideBearing>0) {shape.fallback=true;return S_OK;}
            }
            GlyphRun result; result.face=run->fontFace; result.x=x;result.y=y;result.em=run->fontEmSize;
            size_t count=run->glyphCount;
            size_t advancesOffset=(count*sizeof(UINT16)+alignof(float)-1)&~(alignof(float)-1);
            size_t offsetsOffset=advancesOffset+count*sizeof(float);
            auto data=static_cast<std::byte*>(shape.arena.allocate(offsetsOffset+count*sizeof(DWRITE_GLYPH_OFFSET),alignof(DWRITE_GLYPH_OFFSET)));
            result.glyphs={reinterpret_cast<UINT16*>(data),count};
            result.advances={reinterpret_cast<float*>(data+advancesOffset),count};
            result.offsets={reinterpret_cast<DWRITE_GLYPH_OFFSET*>(data+offsetsOffset),count};
            std::memcpy(result.glyphs.data(),run->glyphIndices,count*sizeof(UINT16));
            if(run->glyphAdvances)std::memcpy(result.advances.data(),run->glyphAdvances,count*sizeof(float));else std::fill(result.advances.begin(),result.advances.end(),0.f);
            if(run->glyphOffsets)std::memcpy(result.offsets.data(),run->glyphOffsets,count*sizeof(DWRITE_GLYPH_OFFSET));else std::fill(result.offsets.begin(),result.offsets.end(),DWRITE_GLYPH_OFFSET{});
            shape.runs.push_back(std::move(result));return S_OK;
        } catch(const std::bad_alloc&) {return E_OUTOFMEMORY;} catch(...) {return E_FAIL;}
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*,float,float,const DWRITE_UNDERLINE*,IUnknown*) override {shape.fallback=true;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*,float,float,const DWRITE_STRIKETHROUGH*,IUnknown*) override {shape.fallback=true;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*,float,float,IDWriteInlineObject*,BOOL,BOOL,IUnknown*) override {shape.fallback=true;return S_OK;}
};
}

#include "renderer-dx12.h"

static_assert(sizeof(OtrStats)==144);
static_assert(sizeof(OtrProfile)==184);
static_assert(sizeof(OtrLifetimeStats)==64);

void Dx12Device::initializePipelines() {
        D3D12_INPUT_ELEMENT_DESC elements[]={{"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},{"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,8,D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},{"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,16,D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},{"SHAPE",0,DXGI_FORMAT_R32_FLOAT,0,32,D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0}};
        for(UINT filtering=0;filtering<2;filtering++) {
            D3D12_DESCRIPTOR_RANGE range{D3D12_DESCRIPTOR_RANGE_TYPE_SRV,1,0,0,D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND};D3D12_ROOT_PARAMETER parameter{};parameter.ParameterType=D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;parameter.DescriptorTable={1,&range};parameter.ShaderVisibility=D3D12_SHADER_VISIBILITY_PIXEL;
            D3D12_STATIC_SAMPLER_DESC sampler{};sampler.Filter=filtering?D3D12_FILTER_MIN_MAG_MIP_LINEAR:D3D12_FILTER_MIN_MAG_MIP_POINT;sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D12_TEXTURE_ADDRESS_MODE_CLAMP;sampler.MaxAnisotropy=1;sampler.ComparisonFunc=D3D12_COMPARISON_FUNC_ALWAYS;sampler.MaxLOD=D3D12_FLOAT32_MAX;sampler.ShaderVisibility=D3D12_SHADER_VISIBILITY_PIXEL;
            D3D12_ROOT_SIGNATURE_DESC root{1,&parameter,1,&sampler,D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};ComPtr<ID3DBlob> serialized,serializationErrors;check(D3D12SerializeRootSignature(&root,D3D_ROOT_SIGNATURE_VERSION_1,&serialized,&serializationErrors));check(device->CreateRootSignature(0,serialized->GetBufferPointer(),serialized->GetBufferSize(),IID_PPV_ARGS(&pipelines.roots[filtering])));
            for(UINT overwrite=0;overwrite<2;overwrite++) {
                D3D12_GRAPHICS_PIPELINE_STATE_DESC pipeline{};pipeline.pRootSignature=pipelines.roots[filtering].Get();pipeline.VS={OtrVertexShader,sizeof(OtrVertexShader)};pipeline.PS={OtrPixelShader,sizeof(OtrPixelShader)};pipeline.InputLayout={elements,4};pipeline.PrimitiveTopologyType=D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;pipeline.NumRenderTargets=1;pipeline.RTVFormats[0]=DXGI_FORMAT_B8G8R8A8_UNORM;pipeline.SampleDesc.Count=1;pipeline.SampleMask=UINT_MAX;
                pipeline.RasterizerState.FillMode=D3D12_FILL_MODE_SOLID;pipeline.RasterizerState.CullMode=D3D12_CULL_MODE_NONE;pipeline.RasterizerState.DepthClipEnable=TRUE;
                pipeline.DepthStencilState.DepthEnable=FALSE;pipeline.DepthStencilState.StencilEnable=FALSE;pipeline.DepthStencilState.DepthFunc=D3D12_COMPARISON_FUNC_ALWAYS;
                auto& blend=pipeline.BlendState.RenderTarget[0];blend.BlendEnable=overwrite?FALSE:TRUE;blend.SrcBlend=blend.SrcBlendAlpha=D3D12_BLEND_ONE;blend.DestBlend=blend.DestBlendAlpha=D3D12_BLEND_INV_SRC_ALPHA;blend.BlendOp=blend.BlendOpAlpha=D3D12_BLEND_OP_ADD;blend.LogicOp=D3D12_LOGIC_OP_NOOP;blend.RenderTargetWriteMask=D3D12_COLOR_WRITE_ENABLE_ALL;
                check(device->CreateGraphicsPipelineState(&pipeline,IID_PPV_ARGS(&pipelines.states[filtering][overwrite])));
            }
        }
        pipelineBuilds++;
}


struct OtrTexture {
    std::atomic<uint32_t> references{1};
    void AddRef() noexcept {references.fetch_add(1,std::memory_order_relaxed);}
    void Release() noexcept {if(references.fetch_sub(1,std::memory_order_acq_rel)==1)delete this;}
    std::shared_ptr<Dx12Device> gpu;
    ComPtr<ID3D12Resource> resource;
    Dx12Descriptor srv,rtv;
    D3D12_RESOURCE_STATES state=D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D11Texture2D> texture,external;
    bool acquired=false;
    bool rasterOwned=false;
    std::shared_ptr<TextureAllocation> allocation;
    uint32_t width=0,height=0;
    float dpi=96;
    uint64_t bytes=0;
};
struct TextureRelease {void operator()(OtrTexture* texture) const noexcept {if(texture)texture->Release();}};
using TextureOwner=std::unique_ptr<OtrTexture,TextureRelease>;

struct OtrRenderer {
    std::shared_ptr<Dx12Device> gpu;
    ComPtr<ID3D11Device> device,provider;
    ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<ID3D11Multithread> contextLock;
    ComPtr<ID2D1DeviceContext> raster;
    ComPtr<ID2D1Multithread> interopLock;
    ComPtr<ID2D1Multithread> providerLock;
    ComPtr<ID2D1SolidColorBrush> brush;
    ComPtr<IDWriteFactory> write;
    ComPtr<ID3D12InfoQueue> debug;
    ComPtr<IDXGISwapChain3> swapchain;
    std::array<Dx12Submission,3> submissions;
    size_t slot=0;
    TextureOwner white;
    std::array<TextureOwner,2> buffers;
    OtrTexture* backbuffer=nullptr;
    std::shared_ptr<Accounting> accounting=std::make_shared<Accounting>();
    struct Page {
        TextureOwner texture;
        ComPtr<ID2D1Bitmap1> bitmap;
        uint32_t x=1,y=1,shelf=0;
    };
    struct Entry {
        ComPtr<IUnknown> owner;
        uint32_t page;
        OtrRect source;
        float left,top;
    };
    std::array<Page,AtlasPages> pages;
    std::unordered_map<AtlasKey,Entry,AtlasHash> atlas;
    std::unordered_map<IDWriteTextLayout*,std::unique_ptr<Shape>> shapes;
    size_t shapeBytes=0;
    uint32_t nextPage=0;
    bool layoutModeForCheck=false;
    bool forcedLayoutAtlasUsedForCheck=false;
    OtrStats stats{};
    OtrProfile profile{};
    LifetimeRef<OtrTexture> current,batchTexture;
    std::vector<Vertex> batch;
    D3D12_RECT clip{};
    bool batchLinear=false,batchCopy=false,active=false,recorded=false,transparent=false;
    uint32_t width=0,height=0;
    float dpi=96;
    HANDLE frameLatency=nullptr;
    bool frameReady=false;
    bool frameRecording=false;

    ~OtrRenderer(){
        try {if(gpu)gpu->idle();}catch(...){}
        for(auto& submission:submissions)submission.abandonAfterIdle();
        if(frameLatency)CloseHandle(frameLatency);
    }
    Dx12Submission& command() {
        check(stats.device_removed_reason);
        auto& work=submissions[slot];if(!work.open)work.reset(*gpu,profile);return work;
    }
    void execute() {
        if(submissions[slot].open){submissions[slot].submit(*gpu,profile);slot=(slot+1)%submissions.size();}
    }
    void transition(OtrTexture* target,D3D12_RESOURCE_STATES state) {
        if(target->state==state)return;
        auto& work=command();D3D12_RESOURCE_BARRIER barrier{};barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition={target->resource.Get(),D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,target->state,state};
        work.list->ResourceBarrier(1,&barrier);work.retain(target);target->state=state;
    }
    TextureOwner wrap12(ComPtr<ID3D12Resource> resource,float textureDpi,bool target,bool counted) {
        auto result=TextureOwner(new OtrTexture());result->gpu=gpu;result->resource=resource;result->dpi=textureDpi;
        auto desc=resource->GetDesc();result->width=static_cast<uint32_t>(desc.Width);result->height=desc.Height;
        if(desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM || desc.SampleDesc.Count!=1)throw Failure{E_INVALIDARG};
        result->srv=gpu->descriptor(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12_SHADER_RESOURCE_VIEW_DESC view{};view.Format=desc.Format;view.ViewDimension=D3D12_SRV_DIMENSION_TEXTURE2D;view.Shader4ComponentMapping=D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;view.Texture2D.MipLevels=1;
        gpu->device->CreateShaderResourceView(resource.Get(),&view,result->srv->cpu());
        if(target){result->rtv=gpu->descriptor(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);gpu->device->CreateRenderTargetView(resource.Get(),nullptr,result->rtv->cpu());}
        if(counted){result->bytes=desc.Width*desc.Height*4;result->allocation=std::make_shared<TextureAllocation>(accounting,result->bytes);}
        return result;
    }
    void ensureWrapped(OtrTexture* target) {
        if(target->texture)return;
        D3D11_RESOURCE_FLAGS flags{};flags.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        check(gpu->on12->CreateWrappedResource(target->resource.Get(),&flags,D3D12_RESOURCE_STATE_RENDER_TARGET,D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,IID_PPV_ARGS(&target->texture)));
        target->acquired=true;
    }
    TextureOwner texture(uint32_t w,uint32_t h,float textureDpi,const uint8_t* pixels,bool target,bool rasterTarget=false) {
        if(!w || !h || w>D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION || h>D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION || !std::isfinite(textureDpi) || textureDpi<=0)throw Failure{E_INVALIDARG};
        D3D12_RESOURCE_DESC desc{};desc.Dimension=D3D12_RESOURCE_DIMENSION_TEXTURE2D;desc.Width=w;desc.Height=h;desc.DepthOrArraySize=desc.MipLevels=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Flags=target?D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET:D3D12_RESOURCE_FLAG_NONE;
        auto properties=heap(D3D12_HEAP_TYPE_DEFAULT);ComPtr<ID3D12Resource> resource;
        ComPtr<ID3D11Texture2D> bridgeTexture;
        if(rasterTarget) {
            D3D11_TEXTURE2D_DESC image{};image.Width=w;image.Height=h;image.MipLevels=image.ArraySize=1;image.Format=desc.Format;image.SampleDesc.Count=1;image.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
            check(device->CreateTexture2D(&image,nullptr,&bridgeTexture));
            check(gpu->on12->UnwrapUnderlyingResource(bridgeTexture.Get(),gpu->queue.Get(),IID_PPV_ARGS(&resource)));
            immediate->Flush();
        }else {D3D12_CLEAR_VALUE clear{};clear.Format=desc.Format;check(gpu->device->CreateCommittedResource(&properties,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_COMMON,target?&clear:nullptr,IID_PPV_ARGS(&resource)));}
        auto result=wrap12(resource,textureDpi,target,true);
        result->texture=bridgeTexture;result->rasterOwned=rasterTarget;
        if(pixels) {
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};UINT64 bytes=0;gpu->device->GetCopyableFootprints(&desc,0,1,0,&footprint,nullptr,nullptr,&bytes);
            auto uploadProperties=heap(D3D12_HEAP_TYPE_UPLOAD);auto uploadDesc=bufferDesc(bytes);ComPtr<ID3D12Resource> upload;
            check(gpu->device->CreateCommittedResource(&uploadProperties,D3D12_HEAP_FLAG_NONE,&uploadDesc,D3D12_RESOURCE_STATE_GENERIC_READ,nullptr,IID_PPV_ARGS(&upload)));
            uint8_t* data=nullptr;D3D12_RANGE read{0,0};check(upload->Map(0,&read,reinterpret_cast<void**>(&data)));
            for(UINT y=0;y<h;y++)std::memcpy(data+footprint.Offset+static_cast<size_t>(y)*footprint.Footprint.RowPitch,pixels+static_cast<size_t>(y)*w*4,static_cast<size_t>(w)*4);upload->Unmap(0,nullptr);
            transition(result.get(),D3D12_RESOURCE_STATE_COPY_DEST);auto& work=command();D3D12_TEXTURE_COPY_LOCATION dst{},src{};dst.pResource=resource.Get();dst.Type=D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;src.pResource=upload.Get();src.Type=D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;src.PlacedFootprint=footprint;
            work.list->CopyTextureRegion(&dst,0,0,0,&src,nullptr);work.retain(upload.Get());transition(result.get(),D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);execute();stats.uploaded_bytes+=static_cast<uint64_t>(w)*h*4;
        }
        return result;
    }
    void initialize(void* canvas) {
        auto original=unwrap<ID2D1Device2>(canvas);ComPtr<IDXGIDevice> dxgi;check(original->GetDxgiDevice(&dxgi));check(dxgi.As(&provider));
        if(provider->GetFeatureLevel()<D3D_FEATURE_LEVEL_10_0)throw Failure{DXGI_ERROR_UNSUPPORTED};
        ComPtr<ID2D1Factory> originalFactory;original->GetFactory(&originalFactory);check(originalFactory.As(&providerLock));
        gpu=Dx12Device::get(provider.Get());device=gpu->bridge;immediate=gpu->bridgeContext;interopLock=gpu->lock;debug=gpu->debug;
        if(gpu->profiling)profile.renderer_private_before=profilePrivate();
        ProfileScope pipelineTimer(gpu->profiling,profile.pipeline_ticks);
        check(immediate.As(&contextLock));check(gpu->d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&raster));
        check(raster->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White),&brush));raster->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);raster->SetUnitMode(D2D1_UNIT_MODE_PIXELS);
        check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(write.GetAddressOf())));
        stats.feature_level=D3D_FEATURE_LEVEL_11_0;stats.debug_layer=debug?1u:0u;
        const uint8_t pixel[]={255,255,255,255};white=texture(1,1,96,pixel,false);batch.reserve(4096);
        if(gpu->profiling)profile.renderer_private_after=profilePrivate();
    }
    void flush() {
        if(batch.empty())return;if(!active || !current || !batchTexture || !current->rtv)throw Failure{E_UNEXPECTED};
        transition(current,D3D12_RESOURCE_STATE_RENDER_TARGET);transition(batchTexture,D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        UINT bytes=static_cast<UINT>(batch.size()*sizeof(Vertex));if(!command().hasRoom(bytes))execute();
        auto& work=command();auto allocation=work.allocate(*gpu,profile,bytes);std::memcpy(allocation.second,batch.data(),bytes);
        auto rt=current->rtv->cpu();D3D12_VIEWPORT viewport{0,0,static_cast<float>(current->width),static_cast<float>(current->height),0,1};D3D12_RECT scissor{0,0,static_cast<LONG>(current->width),static_cast<LONG>(current->height)};
        auto list=work.list.Get();
        if(work.boundTarget!=current->resource.Get()){list->OMSetRenderTargets(1,&rt,FALSE,nullptr);list->RSSetViewports(1,&viewport);list->RSSetScissorRects(1,&scissor);work.boundTarget=current->resource.Get();}
        auto root=gpu->pipelines.roots[batchLinear?1:0].Get();if(work.boundRoot!=root){list->SetGraphicsRootSignature(root);work.boundRoot=root;}
        auto pipeline=gpu->pipelines.states[batchLinear?1:0][batchCopy?1:0].Get();if(work.boundPipeline!=pipeline){if(!work.boundPipeline)list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);list->SetPipelineState(pipeline);work.boundPipeline=pipeline;}
        auto heap=batchTexture->srv->page->heap.Get();if(work.boundHeap!=heap){ID3D12DescriptorHeap* heaps[]={heap};list->SetDescriptorHeaps(1,heaps);work.boundHeap=heap;}
        list->SetGraphicsRootDescriptorTable(0,batchTexture->srv->gpu());D3D12_VERTEX_BUFFER_VIEW vertices{allocation.first,bytes,sizeof(Vertex)};list->IASetVertexBuffers(0,1,&vertices);list->DrawInstanced(static_cast<UINT>(batch.size()),1,0,0);
        work.retain(static_cast<OtrTexture*>(current));work.retain(static_cast<OtrTexture*>(batchTexture));
        stats.draw_calls++;stats.vertices+=batch.size();batch.clear();
    }
    void submit() {flush();execute();}
    std::vector<uint8_t> readPixels(OtrTexture* source) {
        submit();auto original=source->state;transition(source,D3D12_RESOURCE_STATE_COPY_SOURCE);
        auto description=source->resource->GetDesc();D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};UINT64 bytes=0;gpu->device->GetCopyableFootprints(&description,0,1,0,&footprint,nullptr,nullptr,&bytes);
        auto properties=heap(D3D12_HEAP_TYPE_READBACK);auto desc=bufferDesc(bytes);ComPtr<ID3D12Resource> readback;check(gpu->device->CreateCommittedResource(&properties,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&readback)));
        auto& work=command();D3D12_TEXTURE_COPY_LOCATION dst{},src{};dst.pResource=readback.Get();dst.Type=D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;dst.PlacedFootprint=footprint;src.pResource=source->resource.Get();src.Type=D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;work.list->CopyTextureRegion(&dst,0,0,0,&src,nullptr);work.retain(readback.Get());work.retain(source);transition(source,original);execute();gpu->wait(submissions[(slot+submissions.size()-1)%submissions.size()].fence);
        std::vector<uint8_t> pixels(static_cast<size_t>(source->width)*source->height*4);void* data=nullptr;D3D12_RANGE range{0,static_cast<SIZE_T>(bytes)};check(readback->Map(0,&range,&data));for(UINT y=0;y<source->height;y++)std::memcpy(pixels.data()+static_cast<size_t>(y)*source->width*4,static_cast<uint8_t*>(data)+footprint.Offset+static_cast<size_t>(y)*footprint.Footprint.RowPitch,static_cast<size_t>(source->width)*4);D3D12_RANGE written{0,0};readback->Unmap(0,&written);return pixels;
    }
    void copyExternal(OtrTexture* target) {
        if(!target->external)return;auto pixels=readPixels(target);
        D2DLock factoryProtection(providerLock.Get());auto original=provider;
        ComPtr<ID3D11DeviceContext> context;original->GetImmediateContext(&context);ComPtr<ID3D11Multithread> gate;check(context.As(&gate));D3DLock protection(gate.Get());context->UpdateSubresource(target->external.Get(),0,nullptr,pixels.data(),target->width*4,0);context->Flush();
    }
    void select(OtrTexture* image,bool filtering,bool overwrite) {
        if(!active) throw Failure{E_UNEXPECTED};
        if(batchTexture!=image || batchLinear!=filtering || batchCopy!=overwrite || batch.size()+32>MaxBatchVertices) flush();
        batchTexture=image; batchLinear=filtering; batchCopy=overwrite;
    }
    Vertex vertex(float x,float y,float u,float v,Color c,float shape=0) {
        return {x*current->dpi/96/current->width*2-1,1-y*current->dpi/96/current->height*2,u,v,c.r,c.g,c.b,c.a,shape};
    }
    void quad(OtrTexture* image,OtrRect dst,OtrRect src,Color c,bool filtering,bool overwrite=false,float shape=0) {
        if(dst.width<=0 || dst.height<=0 || src.width<=0 || src.height<=0) return;
        // Clip axis-aligned quads before upload. Row clips therefore do not
        // break otherwise compatible glyph batches into one draw call per row.
        float units=96/current->dpi;
        float x0=std::max(dst.x,clip.left*units),y0=std::max(dst.y,clip.top*units);
        float x1=std::min(dst.x+dst.width,clip.right*units),y1=std::min(dst.y+dst.height,clip.bottom*units);
        if(x1<=x0 || y1<=y0)return;
        OtrRect cropped{src.x+(x0-dst.x)/dst.width*src.width,src.y+(y0-dst.y)/dst.height*src.height,(x1-x0)/dst.width*src.width,(y1-y0)/dst.height*src.height};
        dst={x0,y0,x1-x0,y1-y0};src=cropped;
        select(image,filtering,overwrite);
        float u0=src.x/image->width,v0=src.y/image->height,u1=(src.x+src.width)/image->width,v1=(src.y+src.height)/image->height;
        auto a=vertex(dst.x,dst.y,u0,v0,c,shape),b=vertex(dst.x+dst.width,dst.y,u1,v0,c,shape),d=vertex(dst.x,dst.y+dst.height,u0,v1,c,shape),e=vertex(dst.x+dst.width,dst.y+dst.height,u1,v1,c,shape);
        batch.insert(batch.end(),{a,b,d,d,b,e});
    }
    void setClip(OtrRect rect) {
        float scale=current->dpi/96;
        clip={std::max(0L,static_cast<LONG>(std::floor(rect.x*scale))),std::max(0L,static_cast<LONG>(std::floor(rect.y*scale))),
            std::min(static_cast<LONG>(current->width),static_cast<LONG>(std::ceil((rect.x+rect.width)*scale))),std::min(static_cast<LONG>(current->height),static_cast<LONG>(std::ceil((rect.y+rect.height)*scale)))};
    }
    void triangle(Vertex a,Vertex b,Vertex c) {
        float left=static_cast<float>(clip.left)/current->width*2-1,right=static_cast<float>(clip.right)/current->width*2-1;
        float top=1-static_cast<float>(clip.top)/current->height*2,bottom=1-static_cast<float>(clip.bottom)/current->height*2;
        auto inside=[&](const Vertex& v){return v.x>=left && v.x<=right && v.y>=bottom && v.y<=top;};
        if(inside(a) && inside(b) && inside(c)){batch.insert(batch.end(),{a,b,c});return;}
        std::array<Vertex,12> polygon{},next{};polygon[0]=a;polygon[1]=b;polygon[2]=c;size_t count=3;
        for(int edge=0;edge<4 && count;edge++) {
            auto distance=[&](const Vertex& v){switch(edge){case 0:return v.x-left;case 1:return right-v.x;case 2:return v.y-bottom;default:return top-v.y;}};
            size_t output=0;Vertex previous=polygon[count-1];float previousDistance=distance(previous);
            for(size_t i=0;i<count;i++) {
                Vertex value=polygon[i];float d=distance(value);
                if((d>=0)!=(previousDistance>=0)) {
                    float t=previousDistance/(previousDistance-d);
                    auto mix=[&](float p,float q){return p+(q-p)*t;};
                    next[output++]={mix(previous.x,value.x),mix(previous.y,value.y),mix(previous.u,value.u),mix(previous.v,value.v),mix(previous.r,value.r),mix(previous.g,value.g),mix(previous.b,value.b),mix(previous.a,value.a),previous.shape};
                }
                if(d>=0)next[output++]=value;
                previous=value;previousDistance=d;
            }
            polygon=next;count=output;
        }
        for(size_t i=1;i+1<count;i++)batch.insert(batch.end(),{polygon[0],polygon[i],polygon[i+1]});
    }
    void begin(OtrTexture* target) {
        if(active) throw Failure{E_UNEXPECTED};
        current=target?target:backbuffer; if(!current || !current->rtv) throw Failure{E_UNEXPECTED};
        active=true; clip={0,0,static_cast<LONG>(current->width),static_cast<LONG>(current->height)};
    }
    void end() {
        if(!active)throw Failure{E_UNEXPECTED};
        // A pane's row layers and final composition belong to one ordered
        // frame. Keep recording across target switches; standalone/diagnostic
        // targets still publish immediately for other renderer consumers.
        flush();if(!frameRecording || current->external)execute();
        copyExternal(current);active=false;current=nullptr;batchTexture=nullptr;
    }
    Page& page(uint32_t index,uint32_t widthNeeded=1,uint32_t heightNeeded=1) {
        auto& p=pages[index]; uint32_t side=std::max(widthNeeded,heightNeeded)+2>1024?AtlasSide:1024;
        if(p.texture && p.texture->width>=side)return p;
        if(p.texture) {
            // Start small for ordinary terminal glyphs; grow a page only when
            // a wide color/italic run needs it. Never exceed two 2048 pages.
            for(auto it=atlas.begin();it!=atlas.end();) {if(it->second.page==index)it=atlas.erase(it);else ++it;}
            stats.atlas_bytes-=static_cast<uint64_t>(p.texture->width)*p.texture->height*4;
            p.bitmap.Reset();p.texture.reset();p.x=p.y=1;p.shelf=0;stats.atlas_evictions++;
        }
        p.texture=texture(side,side,96,nullptr,true,true);
        ensureWrapped(p.texture.get());ComPtr<IDXGISurface> surface; check(p.texture->texture.As(&surface));
        auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
        check(raster->CreateBitmapFromDxgiSurface(surface.Get(),&props,&p.bitmap));
        stats.atlas_bytes+=static_cast<uint64_t>(side)*side*4;
        return p;
    }
    bool reserve(Page& p,uint32_t w,uint32_t h,uint32_t& x,uint32_t& y) {
        uint32_t side=p.texture->width;
        if(p.x+w+1>side) { p.x=1; p.y+=p.shelf+1; p.shelf=0; }
        if(p.y+h+1>side) return false;
        x=p.x; y=p.y; p.x+=w+1; p.shelf=std::max(p.shelf,h); return true;
    }
    void textLayout(IDWriteTextLayout* layout,float x,float y,uint32_t argb) {
        float scale=current->dpi/96;
        float phaseX=x*scale-std::floor(x*scale),phaseY=y*scale-std::floor(y*scale);
        uint32_t opaque=argb|0xff000000;float opacity=static_cast<float>(argb>>24)/255;
        AtlasKey key{layout,opaque,current->dpi,0,0,phaseX,phaseY};
        auto found=atlas.find(key);
        if(found==atlas.end()) {
            stats.atlas_misses++;
            DWRITE_TEXT_METRICS metrics{}; DWRITE_OVERHANG_METRICS overhang{};
            check(layout->GetMetrics(&metrics)); check(layout->GetOverhangMetrics(&overhang));
            float left=std::floor(std::min(0.f,-overhang.left)*scale)-2;
            float top=std::floor(std::min(0.f,-overhang.top)*scale)-2;
            // Overhangs are relative to the layout box, not the text advance.
            // Negative right/bottom overhangs are essential for combining marks
            // extending beyond a narrow advance inside our 100000-DIP layout.
            float right=std::max(metrics.left+metrics.widthIncludingTrailingWhitespace,layout->GetMaxWidth()+overhang.right);
            float bottom=std::max(metrics.top+metrics.height,layout->GetMaxHeight()+overhang.bottom);
            uint32_t w=static_cast<uint32_t>(std::max(1.f,std::ceil(right*scale)-left+2));
            uint32_t h=static_cast<uint32_t>(std::max(1.f,std::ceil(bottom*scale)-top+2));
            // Very wide shaped runs are split into atlas-sized strips without
            // reshaping or truncating their DirectWrite glyph sequence.
            if(w+2>AtlasSide || h+2>AtlasSide) { oversized(layout,x,y,argb,left,top,w,h); return; }
            submit();
            uint32_t slotX=0,slotY=0,index=nextPage;
            bool fits=false;
            for(uint32_t attempt=0;attempt<AtlasPages;attempt++) {
                index=(nextPage+attempt)%AtlasPages;
                if(reserve(page(index,w,h),w,h,slotX,slotY)) {fits=true;break;}
            }
            if(!fits) {
                index=nextPage; auto& recycled=page(index); recycled.x=recycled.y=1; recycled.shelf=0;
                for(auto it=atlas.begin();it!=atlas.end();) { if(it->second.page==index) it=atlas.erase(it); else ++it; }
                stats.atlas_evictions++; if(!reserve(recycled,w,h,slotX,slotY)) throw Failure{E_UNEXPECTED};
                nextPage=(index+1)%AtlasPages;
            }
            if(atlas.size()>=MaxAtlasEntries) atlas.erase(atlas.begin());
            auto& p=page(index); rasterize(p.texture.get(),p.bitmap.Get(),layout,opaque,left-phaseX,top-phaseY,slotX,slotY,w,h,scale);
            Entry entry{ComPtr<IUnknown>(layout),index,{static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(w),static_cast<float>(h)},left,top};
            found=atlas.emplace(key,std::move(entry)).first;
        } else stats.atlas_hits++;
        const auto& entry=found->second;
        quad(pages[entry.page].texture.get(),{(std::floor(x*scale)+entry.left)/scale,(std::floor(y*scale)+entry.top)/scale,entry.source.width/scale,entry.source.height/scale},entry.source,{opacity,opacity,opacity,opacity},false);
    }
    struct RasterStateScope {
        OtrRenderer& renderer;OtrTexture* target;
        D2DLock protection;
        explicit RasterStateScope(OtrRenderer& owner,OtrTexture* texture):renderer(owner),target(texture),protection(owner.interopLock.Get()) {
            ProfileScope timer(renderer.gpu->profiling,renderer.profile.raster_transfer_ticks);renderer.profile.raster_transfers++;
            if(target->rasterOwned) {
                renderer.transition(target,D3D12_RESOURCE_STATE_COMMON);renderer.execute();
                UINT64 value=renderer.gpu->signal();ID3D12Fence* fences[]={renderer.gpu->fence.Get()};
                check(renderer.gpu->on12->ReturnUnderlyingResource(target->texture.Get(),1,&value,fences));return;
            }
            renderer.transition(target,D3D12_RESOURCE_STATE_RENDER_TARGET);renderer.execute();renderer.ensureWrapped(target);
            if(!target->acquired){ID3D11Resource* resources[]={target->texture.Get()};renderer.gpu->on12->AcquireWrappedResources(resources,1);target->acquired=true;}
        }
        ~RasterStateScope(){
            ProfileScope timer(renderer.gpu->profiling,renderer.profile.raster_transfer_ticks);
            if(target->rasterOwned){ComPtr<ID3D12Resource> resource;HRESULT hr=renderer.gpu->on12->UnwrapUnderlyingResource(target->texture.Get(),renderer.gpu->queue.Get(),IID_PPV_ARGS(&resource));renderer.immediate->Flush();target->state=D3D12_RESOURCE_STATE_COMMON;if(FAILED(hr))renderer.stats.device_removed_reason=hr;return;}
            ID3D11Resource* resources[]={target->texture.Get()};renderer.gpu->on12->ReleaseWrappedResources(resources,1);renderer.immediate->Flush();target->acquired=false;target->state=D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        }
    };
    void glyph(IDWriteFontFace* face,float em,UINT16 index,float x,float y,uint32_t argb) {
        float scale=current->dpi/96,physicalX=x*scale,physicalY=y*scale;
        // DirectWrite positions are preserved to 1/64 physical pixel, the same
        // precision used by its outline rasterizer. Integer translation shares
        // one atlas glyph across rows and colors.
        float phaseX=std::round((physicalX-std::floor(physicalX))*64)/64,phaseY=std::round((physicalY-std::floor(physicalY))*64)/64;
        AtlasKey key{face,0,current->dpi,static_cast<uint32_t>(index)+1,em,phaseX,phaseY};
        auto found=atlas.find(key);
        if(found==atlas.end()) {
            stats.atlas_misses++;
            float advance=0; DWRITE_GLYPH_RUN run{face,em,1,&index,&advance,nullptr,FALSE,0};
            ComPtr<IDWriteGlyphRunAnalysis> analysis;
            check(write->CreateGlyphRunAnalysis(&run,scale,nullptr,DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,DWRITE_MEASURING_MODE_NATURAL,phaseX/scale,phaseY/scale,&analysis));
            RECT bounds{}; check(analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_CLEARTYPE_3x1,&bounds));
            if(bounds.left==bounds.right || bounds.top==bounds.bottom) {
                if(atlas.size()>=MaxAtlasEntries)atlas.erase(atlas.begin());
                atlas.emplace(key,Entry{ComPtr<IUnknown>(face),0,{0,0,0,0},0,0}); return;
            }
            bounds.left-=2; bounds.top-=2; bounds.right+=2; bounds.bottom+=2;
            uint32_t w=static_cast<uint32_t>(bounds.right-bounds.left),h=static_cast<uint32_t>(bounds.bottom-bounds.top);
            if(w+2>AtlasSide || h+2>AtlasSide)throw Failure{E_INVALIDARG};
            submit(); uint32_t slotX=0,slotY=0,pageIndex=nextPage; bool fits=false;
            for(uint32_t attempt=0;attempt<AtlasPages;attempt++) {
                pageIndex=(nextPage+attempt)%AtlasPages;
                if(reserve(page(pageIndex,w,h),w,h,slotX,slotY)){fits=true;break;}
            }
            if(!fits) {
                pageIndex=nextPage; auto& p=page(pageIndex); p.x=p.y=1;p.shelf=0;
                for(auto it=atlas.begin();it!=atlas.end();) {if(it->second.page==pageIndex)it=atlas.erase(it);else ++it;}
                stats.atlas_evictions++; if(!reserve(p,w,h,slotX,slotY))throw Failure{E_UNEXPECTED}; nextPage=(pageIndex+1)%AtlasPages;
            }
            auto& p=page(pageIndex);RasterStateScope state(*this,p.texture.get());ProfileScope timer(gpu->profiling,profile.raster_draw_ticks);raster->SetTarget(p.bitmap.Get()); raster->SetTransform(D2D1::Matrix3x2F::Identity());raster->BeginDraw();
            raster->PushAxisAlignedClip(D2D1::RectF(static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(slotX+w),static_cast<float>(slotY+h)),D2D1_ANTIALIAS_MODE_ALIASED);
            raster->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_COPY);brush->SetColor(D2D1::ColorF(0,0));raster->FillRectangle(D2D1::RectF(static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(slotX+w),static_cast<float>(slotY+h)),brush.Get());raster->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
            raster->SetTransform(D2D1::Matrix3x2F(scale,0,0,scale,slotX-static_cast<float>(bounds.left)+phaseX,slotY-static_cast<float>(bounds.top)+phaseY));
            brush->SetColor(D2D1::ColorF(D2D1::ColorF::White)); raster->DrawGlyphRun(D2D1::Point2F(0,0),&run,brush.Get(),DWRITE_MEASURING_MODE_NATURAL);
            raster->PopAxisAlignedClip(); HRESULT hr=raster->EndDraw();raster->SetTarget(nullptr);check(hr);
            if(atlas.size()>=MaxAtlasEntries)atlas.erase(atlas.begin());
            found=atlas.emplace(key,Entry{ComPtr<IUnknown>(face),pageIndex,{static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(w),static_cast<float>(h)},static_cast<float>(bounds.left),static_cast<float>(bounds.top)}).first;
        }else stats.atlas_hits++;
        const auto& entry=found->second; if(entry.source.width==0)return;
        quad(pages[entry.page].texture.get(),{(std::floor(physicalX)+entry.left)/scale,(std::floor(physicalY)+entry.top)/scale,entry.source.width/scale,entry.source.height/scale},entry.source,color(argb),false);
    }
    void text(void* wrapper,float x,float y,uint32_t argb) {
        auto layout=unwrap<IDWriteTextLayout>(wrapper);
        raster->SetDpi(current->dpi,current->dpi);
        if(layoutModeForCheck){textLayout(layout.Get(),x,y,argb);return;}
        auto found=shapes.find(layout.Get());
        std::unique_ptr<Shape> transient;
        const Shape* shape=nullptr;
        if(found==shapes.end()) {
            transient=std::make_unique<Shape>();transient->layout=layout;Collector collector(*transient);check(layout->Draw(nullptr,&collector,0,0));
            constexpr size_t MaxShapeBytes=8*1024*1024;
            size_t bytes=transient->bytes();
            if(bytes<=MaxShapeBytes/4) {
                while(!shapes.empty() && (shapes.size()>=MaxAtlasEntries || shapeBytes+bytes>MaxShapeBytes)) {auto old=shapes.begin();shapeBytes-=old->second->bytes();shapes.erase(old);}
                found=shapes.emplace(layout.Get(),std::move(transient)).first;shapeBytes+=bytes;shape=found->second.get();
            }else shape=transient.get();
        }else shape=found->second.get();
        if(shape->fallback){textLayout(layout.Get(),x,y,argb);return;}
        float scale=current->dpi/96;
        for(const auto& run:shape->runs) {
            float pen=x+run.x,baseline=std::floor((y+run.y)*scale+.5f)/scale;
            for(size_t i=0;i<run.glyphs.size();i++) {
                glyph(run.face.Get(),run.em,run.glyphs[i],pen+run.offsets[i].advanceOffset,baseline-run.offsets[i].ascenderOffset,argb);
                pen+=run.advances[i];
            }
        }
    }
    void rasterize(OtrTexture* target,ID2D1Bitmap1* bitmap,IDWriteTextLayout* layout,uint32_t argb,float left,float top,uint32_t slotX,uint32_t slotY,uint32_t w,uint32_t h,float scale) {
        RasterStateScope state(*this,target);
        ProfileScope timer(gpu->profiling,profile.raster_draw_ticks);
        raster->SetTarget(bitmap); raster->SetTransform(D2D1::Matrix3x2F::Identity()); raster->BeginDraw();
        raster->PushAxisAlignedClip(D2D1::RectF(static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(slotX+w),static_cast<float>(slotY+h)),D2D1_ANTIALIAS_MODE_ALIASED);
        raster->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_COPY);brush->SetColor(D2D1::ColorF(0,0));raster->FillRectangle(D2D1::RectF(static_cast<float>(slotX),static_cast<float>(slotY),static_cast<float>(slotX+w),static_cast<float>(slotY+h)),brush.Get());raster->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
        raster->SetTransform(D2D1::Matrix3x2F(scale,0,0,scale,slotX-left,slotY-top));
        brush->SetColor(straight(argb)); raster->DrawTextLayout(D2D1::Point2F(0,0),layout,brush.Get(),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        raster->PopAxisAlignedClip(); HRESULT hr=raster->EndDraw(); raster->SetTarget(nullptr); check(hr);
    }
    void oversized(IDWriteTextLayout* layout,float x,float y,uint32_t argb,float left,float top,uint32_t w,uint32_t h) {
        // This path is bounded to a single temporary texture per strip, even for
        // pathological long rows. Finish each strip before releasing its SRV.
        submit(); float scale=current->dpi/96;
        float originX=x*scale+left,originY=y*scale+top;
        uint32_t firstX=static_cast<uint32_t>(std::max(0.f,static_cast<float>(clip.left)-originX))/(AtlasSide-2)*(AtlasSide-2);
        uint32_t firstY=static_cast<uint32_t>(std::max(0.f,static_cast<float>(clip.top)-originY))/(AtlasSide-2)*(AtlasSide-2);
        uint32_t lastX=static_cast<uint32_t>(std::min(static_cast<float>(w),std::max(0.f,static_cast<float>(clip.right)-originX)));
        uint32_t lastY=static_cast<uint32_t>(std::min(static_cast<float>(h),std::max(0.f,static_cast<float>(clip.bottom)-originY)));
        for(uint32_t sy=firstY;sy<lastY;sy+=AtlasSide-2) for(uint32_t sx=firstX;sx<lastX;sx+=AtlasSide-2) {
            uint32_t sw=std::min(AtlasSide-2,w-sx),sh=std::min(AtlasSide-2,h-sy);
            auto strip=texture(sw,sh,96,nullptr,true,true);ensureWrapped(strip.get()); ComPtr<IDXGISurface> surface; check(strip->texture.As(&surface));
            auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
            ComPtr<ID2D1Bitmap1> bitmap; check(raster->CreateBitmapFromDxgiSurface(surface.Get(),&props,&bitmap));
            rasterize(strip.get(),bitmap.Get(),layout,argb|0xff000000,left+sx,top+sy,0,0,sw,sh,scale);
            float opacity=static_cast<float>(argb>>24)/255;
            quad(strip.get(),{x+(left+sx)/scale,y+(top+sy)/scale,sw/scale,sh/scale},{0,0,static_cast<float>(sw),static_cast<float>(sh)},{opacity,opacity,opacity,opacity},false); submit(); batchTexture=nullptr;
        }
    }
    void resize(uint32_t w,uint32_t h,float newDpi,bool alpha) {
        if(active || !w || !h || !std::isfinite(newDpi) || newDpi<=0)throw Failure{E_INVALIDARG};
        if(swapchain && w==width && h==height){transparent=alpha;if(dpi!=newDpi){dpi=newDpi;for(auto& buffer:buffers)buffer->dpi=dpi;setDpi();}return;}
        submit();gpu->idle();for(auto& work:submissions)work.retireCompleted(*gpu);
        backbuffer=nullptr;for(auto& buffer:buffers)buffer.reset();immediate->ClearState();immediate->Flush();gpu->idle();
        if(!swapchain) {
            ComPtr<IDXGIFactory2> factory;check(CreateDXGIFactory2(0,IID_PPV_ARGS(&factory)));
            // The backing chain has pane lifetime, including alpha changes.
            // Opaque frames contain alpha=1; translucent presentation uses the
            // retained drawing surface. Neither path needs a new DXGI identity.
            DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.Scaling=DXGI_SCALING_STRETCH;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            ComPtr<IDXGISwapChain1> chain;check(factory->CreateSwapChainForComposition(gpu->queue.Get(),&desc,nullptr,&chain));check(chain.As(&swapchain));check(swapchain->SetMaximumFrameLatency(2));frameLatency=swapchain->GetFrameLatencyWaitableObject();if(!frameLatency)throw Failure{E_FAIL};
        }else check(swapchain->ResizeBuffers(2,w,h,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
        for(UINT i=0;i<buffers.size();i++){ComPtr<ID3D12Resource> resource;check(swapchain->GetBuffer(i,IID_PPV_ARGS(&resource)));buffers[i]=wrap12(resource,newDpi,true,true);}
        backbuffer=buffers[swapchain->GetCurrentBackBufferIndex()].get();width=w;height=h;dpi=newDpi;transparent=alpha;setDpi();
    }
    void setDpi() {
        ComPtr<IDXGISwapChain2> chain; check(swapchain.As(&chain));
        DXGI_MATRIX_3X2_F matrix{96/dpi,0,0,96/dpi,0,0}; check(chain->SetMatrixTransform(&matrix));
    }
};

#define OTR_TRY try
#define OTR_CATCH catch(const Failure& failure) { return failure.hr; } catch(const std::bad_alloc&) { return E_OUTOFMEMORY; } catch(...) { return E_FAIL; } return S_OK
int32_t otr_create(void* canvas,OtrRenderer** result) { if(!result)return E_POINTER; *result=nullptr; OTR_TRY { auto r=std::make_unique<OtrRenderer>(); r->initialize(canvas); *result=r.release(); } OTR_CATCH; }
void otr_destroy(OtrRenderer* r) { delete r; }
void otr_shutdown_devices() {Dx12Device::shutdown();}
int32_t otr_resize(OtrRenderer* r,uint32_t w,uint32_t h,float dpi,int32_t alpha) { if(!r)return E_POINTER; OTR_TRY {r->resize(w,h,dpi,alpha!=0);} OTR_CATCH; }
int32_t otr_swapchain(OtrRenderer* r,void** result) { if(!r || !result)return E_POINTER; *result=nullptr; if(!r->swapchain)return E_UNEXPECTED; *result=r->swapchain.Get(); r->swapchain->AddRef(); return S_OK; }
int32_t otr_acquire_frame(OtrRenderer* r) {
    if(!r || !r->frameLatency)return E_UNEXPECTED;if(r->frameReady)return S_OK;
    DWORD wait=WaitForSingleObject(r->frameLatency,0);if(wait==WAIT_TIMEOUT)return S_FALSE;
    if(wait!=WAIT_OBJECT_0)return HRESULT_FROM_WIN32(GetLastError());r->frameReady=true;return S_OK;
}
int32_t otr_begin_frame(OtrRenderer* r) {
    if(!r || r->active || r->frameRecording)return E_UNEXPECTED;
    r->frameRecording=true;return S_OK;
}
int32_t otr_present(OtrRenderer* r) {
    if(!r || !r->swapchain)return E_POINTER;r->frameRecording=false;HRESULT ready=otr_acquire_frame(r);if(ready!=S_OK)return ready;
    OTR_TRY {ProfileScope timer(r->gpu->profiling,r->profile.present_ticks);r->transition(r->backbuffer,D3D12_RESOURCE_STATE_PRESENT);r->execute();HRESULT hr=r->swapchain->Present(0,DXGI_PRESENT_DO_NOT_WAIT);r->stats.device_removed_reason=r->gpu->device->GetDeviceRemovedReason();if(hr==DXGI_ERROR_WAS_STILL_DRAWING)return S_FALSE;r->frameReady=false;check(hr);r->stats.frames++;r->backbuffer=r->buffers[r->swapchain->GetCurrentBackBufferIndex()].get();}OTR_CATCH;
}
int32_t otr_device(OtrRenderer* r,void** result) {if(!r || !result)return E_POINTER;*result=r->device.Get();r->device->AddRef();return S_OK;}
int32_t otr_resize_surface(void* surface,uint32_t width,uint32_t height) {
    if(!surface)return E_POINTER;OTR_TRY{ComPtr<DrawingSurfaceInterop> interop;check(static_cast<IUnknown*>(surface)->QueryInterface(IID_PPV_ARGS(&interop)));check(interop->Resize({static_cast<LONG>(width),static_cast<LONG>(height)}));}OTR_CATCH;
}
int32_t otr_present_surface(OtrRenderer* r,void* surface) {
    if(!r || !surface || !r->backbuffer || r->active)return E_POINTER;
    r->frameRecording=false;
    OTR_TRY {
        D2DLock protection(r->interopLock.Get());
        ComPtr<DrawingSurfaceInterop> interop;check(static_cast<IUnknown*>(surface)->QueryInterface(IID_PPV_ARGS(&interop)));
        ComPtr<ID3D11Texture2D> texture;POINT offset{};check(interop->BeginDraw(nullptr,IID_PPV_ARGS(&texture),&offset));
        struct Update {DrawingSurfaceInterop* surface;bool active=true;~Update(){if(active)surface->EndDraw();}} update{interop.Get()};
        ComPtr<ID3D11Device> owner;texture->GetDevice(&owner);if(owner.Get()!=r->device.Get())throw Failure{E_INVALIDARG};
        D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
        if(offset.x<0 || offset.y<0 || static_cast<uint64_t>(offset.x)+r->width>desc.Width || static_cast<uint64_t>(offset.y)+r->height>desc.Height)throw Failure{E_INVALIDARG};
        {OtrRenderer::RasterStateScope scope(*r,r->backbuffer);D3D11_BOX box{0,0,0,r->width,r->height,1};r->immediate->CopySubresourceRegion(texture.Get(),0,static_cast<UINT>(offset.x),static_cast<UINT>(offset.y),0,r->backbuffer->texture.Get(),0,&box);}
        HRESULT hr=interop->EndDraw();update.active=false;check(hr);r->stats.frames++;
    }OTR_CATCH;
}
int32_t otr_texture_create(OtrRenderer* r,uint32_t w,uint32_t h,float dpi,const uint8_t* pixels,int32_t target,OtrTexture** result) { if(!r || !result)return E_POINTER; *result=nullptr; OTR_TRY {*result=r->texture(w,h,dpi,pixels,target!=0).release();} OTR_CATCH; }
int32_t otr_texture_from_bitmap(OtrRenderer* r,void* bitmap,float dpi,OtrTexture** result) {
    if(!r || !result)return E_POINTER;*result=nullptr;OTR_TRY {
        auto b=unwrap<ID2D1Bitmap1>(bitmap);ComPtr<IDXGISurface> surface;check(b->GetSurface(&surface));ComPtr<ID3D11Texture2D> original;check(surface.As(&original));ComPtr<ID3D11Device> owner;original->GetDevice(&owner);if(owner.Get()!=r->provider.Get())throw Failure{E_INVALIDARG};
        D3D11_TEXTURE2D_DESC desc{};original->GetDesc(&desc);
        std::vector<uint8_t> pixels(static_cast<size_t>(desc.Width)*desc.Height*4);
        {D2DLock protection(r->providerLock.Get());ComPtr<ID3D11DeviceContext> context;owner->GetImmediateContext(&context);ComPtr<ID3D11Multithread> gate;check(context.As(&gate));D3DLock contextProtection(gate.Get());
            auto stagingDesc=desc;stagingDesc.Usage=D3D11_USAGE_STAGING;stagingDesc.BindFlags=0;stagingDesc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;stagingDesc.MiscFlags=0;ComPtr<ID3D11Texture2D> staging;check(owner->CreateTexture2D(&stagingDesc,nullptr,&staging));context->CopyResource(staging.Get(),original.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));for(UINT y=0;y<desc.Height;y++)std::memcpy(pixels.data()+static_cast<size_t>(y)*desc.Width*4,static_cast<uint8_t*>(mapped.pData)+static_cast<size_t>(y)*mapped.RowPitch,static_cast<size_t>(desc.Width)*4);context->Unmap(staging.Get(),0);}
        auto target=r->texture(desc.Width,desc.Height,dpi,pixels.data(),true);target->external=original;*result=target.release();
    }OTR_CATCH;
}
void otr_texture_destroy(OtrTexture* texture) {if(texture)texture->Release();}
int32_t otr_texture_alias_for_check(OtrRenderer* r,OtrTexture* source,OtrTexture** result) {
    if(!r || !source || !result)return E_POINTER;*result=nullptr;
    OTR_TRY {
        if(source->gpu!=r->gpu || source->rtv || source->state!=D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE)throw Failure{E_INVALIDARG};
        auto alias=r->wrap12(source->resource,source->dpi,false,false);alias->state=source->state;alias->allocation=source->allocation;*result=alias.release();
    }OTR_CATCH;
}
int32_t otr_begin(OtrRenderer* r,OtrTexture* target) { if(!r)return E_POINTER; OTR_TRY {r->begin(target);} OTR_CATCH; }
int32_t otr_end(OtrRenderer* r) { if(!r)return E_POINTER; OTR_TRY {r->end();} OTR_CATCH; }
int32_t otr_clear(OtrRenderer* r,uint32_t argb) {if(!r || !r->active)return E_UNEXPECTED;OTR_TRY {r->flush();auto previous=r->clip;r->clip={0,0,static_cast<LONG>(r->current->width),static_cast<LONG>(r->current->height)};r->quad(r->white.get(),{0,0,r->current->width*96/r->current->dpi,r->current->height*96/r->current->dpi},{0,0,1,1},color(argb),false,true);r->flush();r->clip=previous;}OTR_CATCH;}
int32_t otr_clip(OtrRenderer* r,OtrRect rect) { if(!r || !r->active)return E_UNEXPECTED; OTR_TRY {r->setClip(rect);} OTR_CATCH; }
int32_t otr_fill(OtrRenderer* r,OtrRect rect,uint32_t argb,int32_t copy) { if(!r || !r->active)return E_UNEXPECTED; OTR_TRY {r->quad(r->white.get(),rect,{0,0,1,1},color(argb),false,copy!=0);} OTR_CATCH; }
int32_t otr_triangle(OtrRenderer* r,float x0,float y0,float x1,float y1,float x2,float y2,uint32_t argb) { if(!r || !r->active)return E_UNEXPECTED; OTR_TRY {r->select(r->white.get(),false,false); auto c=color(argb); r->triangle(r->vertex(x0,y0,.5f,.5f,c),r->vertex(x1,y1,.5f,.5f,c),r->vertex(x2,y2,.5f,.5f,c));} OTR_CATCH; }
int32_t otr_powerline(OtrRenderer* r,OtrRect rect,uint32_t argb,int32_t shape) {if(!r || !r->active)return E_UNEXPECTED;if(shape<1 || shape>4)return E_INVALIDARG;OTR_TRY{r->quad(r->white.get(),rect,{0,0,1,1},color(argb),false,false,static_cast<float>(shape));}OTR_CATCH;}
int32_t otr_line(OtrRenderer* r,float x0,float y0,float x1,float y1,float width,uint32_t argb) { if(!r || !r->active)return E_UNEXPECTED; OTR_TRY {float dx=x1-x0,dy=y1-y0,length=std::sqrt(dx*dx+dy*dy); if(length>0) {float radius=(width+96/r->current->dpi)/2,nx=-dy/length*radius,ny=dx/length*radius; r->select(r->white.get(),false,false); auto c=color(argb); auto a=r->vertex(x0+nx,y0+ny,0,0,c,5),b=r->vertex(x1+nx,y1+ny,1,0,c,5),d=r->vertex(x0-nx,y0-ny,0,1,c,5),e=r->vertex(x1-nx,y1-ny,1,1,c,5); r->triangle(a,b,d);r->triangle(d,b,e);}} OTR_CATCH; }
int32_t otr_image(OtrRenderer* r,OtrTexture* texture,OtrRect dst,OtrRect src,float opacity,int32_t linear) { if(!r || !texture || !r->active)return E_POINTER; OTR_TRY {if(texture==r->current)throw Failure{E_INVALIDARG}; if(texture->gpu.get()!=r->gpu.get())throw Failure{E_INVALIDARG}; r->quad(texture,dst,src,{opacity,opacity,opacity,opacity},linear!=0);} OTR_CATCH; }
int32_t otr_text(OtrRenderer* r,void* layout,float x,float y,uint32_t argb) { if(!r || !r->active)return E_UNEXPECTED; OTR_TRY {r->text(layout,x,y,argb);} OTR_CATCH; }
int32_t otr_forget_text(OtrRenderer* r,void* wrapper) {
    if(!r)return E_POINTER;OTR_TRY {
        auto layout=unwrap<IDWriteTextLayout>(wrapper);
        auto shape=r->shapes.find(layout.Get());
        bool onlyGlyphMasks=shape!=r->shapes.end() && !shape->second->fallback && !r->forcedLayoutAtlasUsedForCheck;
        if(shape!=r->shapes.end()){r->shapeBytes-=shape->second->bytes();r->shapes.erase(shape);}
        for(auto entry=r->atlas.begin();!onlyGlyphMasks && entry!=r->atlas.end();) {
            if(entry->first.glyph==0 && entry->first.resource==layout.Get())entry=r->atlas.erase(entry);else ++entry;
        }
    }OTR_CATCH;
}
void otr_stats(OtrRenderer* r,OtrStats* result) {
    if(!r || !result)return;
    for(auto& work:r->submissions)work.retireCompleted(*r->gpu);
    r->gpu->trimDescriptors();
    r->stats.texture_bytes=r->accounting->textures;r->stats.atlas_entries=static_cast<uint32_t>(r->atlas.size());r->stats.shape_cache_bytes=r->shapeBytes;
    r->stats.vertex_buffer_bytes=0;for(const auto& work:r->submissions)for(const auto& upload:work.uploads)r->stats.vertex_buffer_bytes+=upload.capacity;
    r->stats.backend_api=12;r->stats.submission_slots=static_cast<uint32_t>(r->submissions.size());r->stats.submissions=r->gpu->executedLists;
    try{r->gpu->collectDiagnostics();}catch(...){r->gpu->errors++;}
    r->stats.debug_errors=r->gpu->errors;r->stats.debug_warnings=r->gpu->warnings;r->stats.interop_warnings=r->gpu->interopWarnings;
    *result=r->stats;
}
void otr_profile(OtrRenderer* r,OtrProfile* result) {
    if(!r || !result)return;
    auto value=r->profile;const auto& device=r->gpu->profile;
    value.frequency=device.frequency;value.device_private_before=device.device_private_before;value.device_private_created=device.device_private_created;value.device_private_bridge=device.device_private_bridge;value.device_private_d2d=device.device_private_d2d;
    value.texture_bytes=r->accounting->textures;
    value.descriptor_heaps=r->gpu->srvPages.size()+r->gpu->rtvPages.size();
    for(const auto& work:r->submissions)for(const auto& upload:work.uploads)value.upload_bytes+=upload.capacity;
    *result=value;
}
void otr_lifetime_stats(OtrRenderer* r,OtrLifetimeStats* result) {
    if(!r || !result)return;OtrLifetimeStats value{};
    for(const auto& work:r->submissions){
        value.submission_arena_bytes+=work.lifetime.arenaBytes();value.pin_requests+=work.lifetime.pinRequests();
        value.unique_pins+=work.lifetime.uniquePins();value.live_pins+=work.lifetime.livePins();
        for(const auto& upload:work.uploads)value.upload_bytes+=upload.capacity;
    }
    value.shape_arena_bytes=r->shapeBytes;value.device_pipeline_builds=r->gpu->pipelineBuilds;
    value.descriptor_pages=r->gpu->srvPages.size()+r->gpu->rtvPages.size();*result=value;
}
void otr_text_mode_for_check(OtrRenderer* r,int32_t mode) {if(r){r->layoutModeForCheck=mode!=0;if(mode)r->forcedLayoutAtlasUsedForCheck=true;}}
uint32_t otr_debug_messages(OtrRenderer* r,uint8_t* destination,uint32_t capacity) {
    if(!r || !r->debug)return 0;
    try{r->gpu->collectDiagnostics();std::string text=r->gpu->diagnostics;if(r->gpu->diagnosticsTruncated)text="[Older diagnostic text truncated; validation totals retain every collected message.]\n"+text;
        uint32_t length=static_cast<uint32_t>(text.size());if(destination && capacity)std::memcpy(destination,text.data(),std::min(length,capacity));return length;
    }catch(...){return 0;}
}
int32_t otr_read_pixels_for_check(OtrRenderer* r,uint8_t* destination,uint64_t capacity) {
    if(!r || !destination)return E_POINTER;OTR_TRY {if(r->active || !r->backbuffer || capacity<static_cast<uint64_t>(r->width)*r->height*4)throw Failure{E_INVALIDARG};auto pixels=r->readPixels(r->backbuffer);std::memcpy(destination,pixels.data(),pixels.size());}OTR_CATCH;
}

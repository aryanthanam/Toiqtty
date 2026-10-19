#pragma once
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <d3d11on12.h>
#include <mutex>
#include <psapi.h>

inline UINT64 profileClock() {LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart;}
inline UINT64 profilePrivate() {PROCESS_MEMORY_COUNTERS_EX value{};value.cb=sizeof(value);return GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&value),sizeof(value))?value.PrivateUsage:0;}
struct ProfileScope {
    UINT64& total;UINT64 start;
    ProfileScope(bool enabled,UINT64& value):total(value),start(enabled?profileClock():0){}
    ~ProfileScope(){if(start)total+=profileClock()-start;}
};

// Descriptor slots are immutable while a recorded/submitted list holds their
// lease. Recycling a texture must never overwrite a descriptor still in flight.
struct Dx12DescriptorPage {
    static constexpr UINT Capacity=1024;
    ComPtr<ID3D12DescriptorHeap> heap;
    UINT increment=0;
    std::mutex gate;
    std::vector<UINT> free;
    explicit Dx12DescriptorPage(ID3D12Device* device,D3D12_DESCRIPTOR_HEAP_TYPE type) {
        D3D12_DESCRIPTOR_HEAP_DESC description{};description.Type=type;description.NumDescriptors=Capacity;
        description.Flags=type==D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV?D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE:D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        check(device->CreateDescriptorHeap(&description,IID_PPV_ARGS(&heap)));increment=device->GetDescriptorHandleIncrementSize(type);
        free.reserve(Capacity);for(UINT i=Capacity;i>0;i--)free.push_back(i-1);
    }
    bool acquire(UINT& slot){std::lock_guard guard(gate);if(free.empty())return false;slot=free.back();free.pop_back();return true;}
    void release(UINT slot){std::lock_guard guard(gate);free.push_back(slot);}
};
struct Dx12Descriptor {
    Dx12Descriptor()=default;
    Dx12Descriptor(const Dx12Descriptor&)=delete;
    Dx12Descriptor& operator=(const Dx12Descriptor&)=delete;
    Dx12Descriptor(Dx12Descriptor&& other) noexcept:page(std::move(other.page)),slot(other.slot){}
    Dx12Descriptor& operator=(Dx12Descriptor&& other) noexcept {if(this!=&other){if(page)page->release(slot);page=std::move(other.page);slot=other.slot;}return *this;}
    std::shared_ptr<Dx12DescriptorPage> page;
    UINT slot=0;
    ~Dx12Descriptor(){if(page)page->release(slot);}
    explicit operator bool() const noexcept {return page!=nullptr;}
    const Dx12Descriptor* operator->() const noexcept {return this;}
    D3D12_CPU_DESCRIPTOR_HANDLE cpu() const {auto handle=page->heap->GetCPUDescriptorHandleForHeapStart();handle.ptr+=static_cast<SIZE_T>(slot)*page->increment;return handle;}
    D3D12_GPU_DESCRIPTOR_HANDLE gpu() const {auto handle=page->heap->GetGPUDescriptorHandleForHeapStart();handle.ptr+=static_cast<UINT64>(slot)*page->increment;return handle;}
};

// Terminal triangles execute directly on this D3D12 queue. The 11-on-12
// device is exclusively the DirectWrite/Direct2D and WinUI surface bridge.
struct Dx12Device {
    bool profiling=GetEnvironmentVariableW(L"OROTERM_RENDER_PROFILE",nullptr,0)!=0;
    OtrProfile profile{};
    std::vector<std::shared_ptr<Dx12DescriptorPage>> srvPages,rtvPages;
    Dx12Descriptor descriptor(D3D12_DESCRIPTOR_HEAP_TYPE type) {
        Dx12Descriptor result;auto& pages=type==D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV?srvPages:rtvPages;
        // Releasing owners can happen on a SafeHandle finalizer. Each page's
        // free list is protected; allocation and pool maintenance stay on XAML.
        for(const auto& page:pages)if(page->acquire(result.slot)){result.page=page;return result;}
        auto page=std::make_shared<Dx12DescriptorPage>(device.Get(),type);pages.push_back(page);
        if(!page->acquire(result.slot))throw Failure{E_UNEXPECTED};result.page=page;return result;
    }
    void trimDescriptors() {
        for(auto* pages:{&srvPages,&rtvPages})
            for(auto it=pages->begin();it!=pages->end();)
                if(pages->size()>1 && it->use_count()==1)it=pages->erase(it);else ++it;
    }
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12InfoQueue> debug;
    ComPtr<ID3D11Device> bridge;
    ComPtr<ID3D11DeviceContext> bridgeContext;
    ComPtr<ID3D11On12Device2> on12;
    ComPtr<ID2D1Device2> d2d;
    ComPtr<ID2D1Multithread> lock;
    struct Pipelines {ComPtr<ID3D12RootSignature> roots[2];ComPtr<ID3D12PipelineState> states[2][2];} pipelines;
    uint64_t pipelineBuilds=0;
    void initializePipelines();

    UINT64 nextFence=0;
    UINT64 executedLists=0;
    UINT64 errors=0,warnings=0,interopWarnings=0;
    std::string diagnostics;
    bool diagnosticsTruncated=false;
    HANDLE completed=nullptr;
    ~Dx12Device() {
        if(queue && fence && SUCCEEDED(device->GetDeviceRemovedReason())) {
            if(bridgeContext){bridgeContext->ClearState();bridgeContext->Flush();}
            UINT64 value=++nextFence;
            if(SUCCEEDED(queue->Signal(fence.Get(),value)) && SUCCEEDED(fence->SetEventOnCompletion(value,completed)))WaitForSingleObject(completed,5000);
        }
        if(completed)CloseHandle(completed);
    }
    void wait(UINT64 value) {
        if(!value || fence->GetCompletedValue()>=value) {
            check(device->GetDeviceRemovedReason());return;
        }
        check(fence->SetEventOnCompletion(value,completed));
        DWORD result=WaitForSingleObject(completed,5000);
        check(device->GetDeviceRemovedReason());
        if(result!=WAIT_OBJECT_0)throw Failure{result==WAIT_TIMEOUT?DXGI_ERROR_DEVICE_HUNG:HRESULT_FROM_WIN32(GetLastError())};
    }
    UINT64 signal() {UINT64 value=++nextFence;check(queue->Signal(fence.Get(),value));return value;}
    void idle() {bridgeContext->Flush();wait(signal());}
    void collectDiagnostics() {
        if(!debug)return;
        if(debug->GetNumMessagesDiscardedByMessageCountLimit())errors++;
        for(UINT64 i=0;i<debug->GetNumStoredMessagesAllowedByRetrievalFilter();i++) {
            SIZE_T size=0;check(debug->GetMessage(i,nullptr,&size));std::vector<uint8_t> storage(size);auto message=reinterpret_cast<D3D12_MESSAGE*>(storage.data());check(debug->GetMessage(i,message,&size));
            if(message->Severity<=D3D12_MESSAGE_SEVERITY_ERROR)errors++;
            if(message->Severity==D3D12_MESSAGE_SEVERITY_WARNING){
                warnings++;
                if(message->ID==D3D12_MESSAGE_ID_CREATERESOURCE_STATE_IGNORED || message->ID==D3D12_MESSAGE_ID_CREATEGRAPHICSPIPELINESTATE_DEPTHSTENCILVIEW_NOT_SET || message->ID==D3D12_MESSAGE_ID_CLEARRENDERTARGETVIEW_MISMATCHINGCLEARVALUE)interopWarnings++;
            }
            if(message->Severity<=D3D12_MESSAGE_SEVERITY_WARNING){
                std::string line=std::to_string(message->ID)+"/"+std::to_string(message->Severity)+": "+message->pDescription+'\n';
                if(diagnostics.size()+line.size()>1024*1024){diagnostics.erase(0,diagnostics.size()/2);diagnosticsTruncated=true;}
                diagnostics+=line;
            }
        }
        // Totals survive draining; an error is never exempted by message ID.
        // Retaining the text tail is bounded separately from validation counts.
        debug->ClearStoredMessages();
    }
    using Cache=std::unordered_map<UINT64,std::shared_ptr<Dx12Device>>;
    static Cache& cache(){static auto* value=new Cache();return *value;}
    static std::mutex& cacheGate(){static auto* value=new std::mutex();return *value;}
    // Explicitly drained on the application UI thread before COM teardown.
    // No D3D fence wait or device release is performed in DLL detach.
    static void shutdown(){std::lock_guard guard(cacheGate());cache().clear();}
    static std::shared_ptr<Dx12Device> get(ID3D11Device* providerDevice) {
        ComPtr<IDXGIDevice> dxgi;check(providerDevice->QueryInterface(IID_PPV_ARGS(&dxgi)));
        ComPtr<IDXGIAdapter> adapter;check(dxgi->GetAdapter(&adapter));DXGI_ADAPTER_DESC description{};check(adapter->GetDesc(&description));
        UINT64 key=static_cast<UINT64>(static_cast<UINT32>(description.AdapterLuid.HighPart))<<32|description.AdapterLuid.LowPart;
        std::lock_guard guard(cacheGate());auto& devices=cache();
        if(auto found=devices.find(key);found!=devices.end()){
            if(SUCCEEDED(found->second->device->GetDeviceRemovedReason()))return found->second;
            devices.erase(found);
        }
        if(devices.size()>=8){for(auto it=devices.begin();it!=devices.end();)if(it->second.use_count()==1)it=devices.erase(it);else ++it;}
        if(devices.size()>=8)throw Failure{DXGI_ERROR_UNSUPPORTED};
        auto result=std::make_shared<Dx12Device>();
        if(result->profiling){LARGE_INTEGER frequency;QueryPerformanceFrequency(&frequency);result->profile.frequency=frequency.QuadPart;result->profile.device_private_before=profilePrivate();}
        bool diagnostics=GetEnvironmentVariableW(L"OROTERM_DIRECTX_DEBUG",nullptr,0)!=0;
        if(diagnostics) {ComPtr<ID3D12Debug> debug;check(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)));debug->EnableDebugLayer();}
        HRESULT hr=D3D12CreateDevice(adapter.Get(),D3D_FEATURE_LEVEL_11_0,IID_PPV_ARGS(&result->device));
        if(FAILED(hr))throw Failure{hr==E_INVALIDARG?DXGI_ERROR_UNSUPPORTED:hr};
        if(result->profiling)result->profile.device_private_created=profilePrivate();
        result->initializePipelines();
        if(diagnostics){check(result->device.As(&result->debug));check(result->debug->SetMessageCountLimit(65536));}
        D3D12_COMMAND_QUEUE_DESC queueDescription{};queueDescription.Type=D3D12_COMMAND_LIST_TYPE_DIRECT;
        check(result->device->CreateCommandQueue(&queueDescription,IID_PPV_ARGS(&result->queue)));
        check(result->device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&result->fence)));
        result->completed=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!result->completed)throw Failure{HRESULT_FROM_WIN32(GetLastError())};
        IUnknown* queues[]={result->queue.Get()};
        check(D3D11On12CreateDevice(result->device.Get(),D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,queues,1,0,&result->bridge,&result->bridgeContext,nullptr));
        check(result->bridge.As(&result->on12));
        if(result->profiling)result->profile.device_private_bridge=profilePrivate();
        ComPtr<ID3D11Multithread> protection;check(result->bridgeContext.As(&protection));protection->SetMultithreadProtected(TRUE);
        ComPtr<ID2D1Factory3> factory;D2D1_FACTORY_OPTIONS options{};
        check(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,__uuidof(ID2D1Factory3),&options,reinterpret_cast<void**>(factory.GetAddressOf())));
        check(factory.As(&result->lock));
        check(result->bridge.As(&dxgi));check(factory->CreateDevice(dxgi.Get(),&result->d2d));
        if(result->profiling)result->profile.device_private_d2d=profilePrivate();
        devices[key]=result;return result;
    }
};

inline D3D12_HEAP_PROPERTIES heap(D3D12_HEAP_TYPE type) {D3D12_HEAP_PROPERTIES value{};value.Type=type;value.CreationNodeMask=value.VisibleNodeMask=1;return value;}
inline D3D12_RESOURCE_DESC bufferDesc(UINT64 bytes) {D3D12_RESOURCE_DESC value{};value.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;value.Width=bytes;value.Height=1;value.DepthOrArraySize=value.MipLevels=1;value.SampleDesc.Count=1;value.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;return value;}

struct Dx12UploadArena {
    static constexpr UINT Limit=4u*1024*1024;
    struct Upload {ComPtr<ID3D12Resource> resource;uint8_t* mapped=nullptr;UINT capacity=0,used=0;};
    std::vector<Upload> uploads;
    bool hasRoom(UINT bytes) const {
        if(bytes>Limit)return false;
        UINT total=0;for(const auto& upload:uploads){if(upload.used)total+=upload.capacity;UINT offset=(upload.used+15u)&~15u;if(offset<=upload.capacity && bytes<=upload.capacity-offset)return true;}
        return total+std::max(65536u,(bytes+65535u)&~65535u)<=Limit;
    }
    std::pair<D3D12_GPU_VIRTUAL_ADDRESS,uint8_t*> allocate(Dx12Device& gpu,OtrProfile& profile,UINT bytes,UINT alignment=16) {
        if(bytes>Limit || !hasRoom(bytes))throw std::bad_alloc();
        for(auto& upload:uploads) {
            UINT offset=(upload.used+alignment-1)&~(alignment-1);
            if(offset<=upload.capacity && bytes<=upload.capacity-offset) {upload.used=offset+bytes;return {upload.resource->GetGPUVirtualAddress()+offset,upload.mapped+offset};}
        }
        // Upload allocations are immutable until this submission's fence has
        // completed. No WRITE_DISCARD or implicit driver resource renaming.
        std::erase_if(uploads,[](const Upload& upload){return upload.used==0;});
        Upload upload;upload.capacity=std::max(65536u,(bytes+65535u)&~65535u);
        ProfileScope timer(gpu.profiling,profile.upload_create_ticks);profile.upload_creates++;
        auto properties=heap(D3D12_HEAP_TYPE_UPLOAD);auto description=bufferDesc(upload.capacity);
        check(gpu.device->CreateCommittedResource(&properties,D3D12_HEAP_FLAG_NONE,&description,D3D12_RESOURCE_STATE_GENERIC_READ,nullptr,IID_PPV_ARGS(&upload.resource)));
        D3D12_RANGE read{0,0};check(upload.resource->Map(0,&read,reinterpret_cast<void**>(&upload.mapped)));
        upload.used=bytes;uploads.push_back(std::move(upload));auto& result=uploads.back();
        return {result.resource->GetGPUVirtualAddress(),result.mapped};
    }
    void resetAfterCompletion() {for(auto& upload:uploads)upload.used=0;}
    auto begin() const {return uploads.begin();}
    auto end() const {return uploads.end();}
};

struct Dx12Submission {
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    Dx12UploadArena uploads;
    SubmissionLifetime lifetime;
    UINT64 fence=0;
    bool open=false;
    ID3D12DescriptorHeap* boundHeap=nullptr;
    ID3D12Resource* boundTarget=nullptr;
    ID3D12RootSignature* boundRoot=nullptr;
    ID3D12PipelineState* boundPipeline=nullptr;
    void reset(Dx12Device& gpu,OtrProfile& profile) {
        ProfileScope timer(gpu.profiling,profile.reset_ticks);profile.resets++;
        if(fence && gpu.fence->GetCompletedValue()<fence){profile.fence_waits++;ProfileScope wait(gpu.profiling,profile.fence_wait_ticks);gpu.wait(fence);}else gpu.wait(fence);
        lifetime.retire();
        uploads.resetAfterCompletion();
        boundHeap=nullptr;
        boundTarget=nullptr;boundRoot=nullptr;boundPipeline=nullptr;
        if(!allocator) {
            check(gpu.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&allocator)));
            check(gpu.device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,allocator.Get(),nullptr,IID_PPV_ARGS(&list)));
        }else {check(allocator->Reset());check(list->Reset(allocator.Get(),nullptr));}
        open=true;
    }
    template<class T> void retain(T* object) {lifetime.retain(object);}
    bool hasRoom(UINT bytes) const {return uploads.hasRoom(bytes);}
    std::pair<D3D12_GPU_VIRTUAL_ADDRESS,uint8_t*> allocate(Dx12Device& gpu,OtrProfile& profile,UINT bytes) {return uploads.allocate(gpu,profile,bytes);}
    bool retireCompleted(Dx12Device& gpu) {
        if(open || (fence && gpu.fence->GetCompletedValue()<fence))return false;
        lifetime.retire();return true;
    }
    void abandonAfterIdle() {
        // Drop an unsubmitted list before releasing its recorded resources.
        list.Reset();allocator.Reset();open=false;lifetime.retire();
    }
    void submit(Dx12Device& gpu,OtrProfile& profile) {
        if(!open)return;ProfileScope timer(gpu.profiling,profile.execute_ticks);check(list->Close());ID3D12CommandList* lists[]={list.Get()};gpu.queue->ExecuteCommandLists(1,lists);profile.lists++;
        gpu.executedLists++;fence=gpu.signal();open=false;
    }
};

// Offline Windows dump inspection through the system debugging engine.
#include <windows.h>
#include <dbgeng.h>
#include <cstdio>

struct DumpOutput : IDebugOutputCallbacks {
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return 1; }
    ULONG STDMETHODCALLTYPE Release() noexcept override { return 1; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) noexcept override {
        if (IsEqualIID(id, __uuidof(IUnknown)) || IsEqualIID(id, __uuidof(IDebugOutputCallbacks))) {
            *value = this; return S_OK;
        }
        *value = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE Output(ULONG, PCSTR text) noexcept override { std::fputs(text, stdout); return S_OK; }
};
int main(int argc, char** argv) {
    if (argc < 2) return 2;
    IDebugClient* client = nullptr;
    HRESULT hr = DebugCreate(__uuidof(IDebugClient), (void**)&client);
    if (FAILED(hr)) return 3;
    DumpOutput output;
    client->SetOutputCallbacks(&output);
    hr = client->OpenDumpFile(argv[1]);
    if (FAILED(hr)) { std::printf("OpenDumpFile: %08lx\n", (unsigned long)hr); return 4; }
    IDebugControl* control = nullptr;
    client->QueryInterface(__uuidof(IDebugControl), (void**)&control);
    hr = control->WaitForEvent(DEBUG_WAIT_DEFAULT, INFINITE);
    if (FAILED(hr)) return 5;
    for (int i = 2; i < argc; ++i)
        control->Execute(DEBUG_OUTCTL_THIS_CLIENT, argv[i], DEBUG_EXECUTE_DEFAULT);
    control->Release(); client->EndSession(DEBUG_END_PASSIVE); client->Release();
    return 0;
}

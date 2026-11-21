#include "Camera.h"

// Keep COM and the session-lifetime camera on one MTA thread, independent of ASP.NET worker threads.
struct CameraHost {
    std::mutex mutex;
    std::condition_variable changed;
    std::thread worker;
    bool ready=false,stop=false;
    HRESULT result=E_PENDING;
    void Run(UINT32 port) noexcept {
        HRESULT apartment=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        HRESULT hr=apartment;
        bool mf=false;
        ComPtr<IMFVirtualCamera> camera;
        if(SUCCEEDED(hr)){hr=MFStartup(MF_VERSION);mf=SUCCEEDED(hr);}
        if(SUCCEEDED(hr))hr=MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,MFVirtualCameraLifetime_Session,
            MFVirtualCameraAccess_CurrentUser,L"PhoneBox Camera",CameraClsid,nullptr,0,&camera);
        if(SUCCEEDED(hr))hr=camera->SetUINT32(CameraPortAttribute,port);
        if(SUCCEEDED(hr))hr=camera->Start(nullptr);
        {
            std::unique_lock guard(mutex);result=hr;ready=true;changed.notify_all();
            if(SUCCEEDED(hr))changed.wait(guard,[&]{return stop;});
        }
        if(camera){camera->Stop();camera->Remove();camera->Shutdown();camera.Reset();}
        if(mf)MFShutdown();
        if(SUCCEEDED(apartment))CoUninitialize();
    }
    ~CameraHost(){ {std::lock_guard guard(mutex);stop=true;changed.notify_all();} if(worker.joinable())worker.join(); }
};

extern "C" HRESULT __stdcall PhoneBoxCameraStart(UINT32 port,void** result) noexcept {
    if(!result)return E_POINTER;*result=nullptr;if(port<1024 || port>65535)return E_INVALIDARG;
    try {
        auto host=std::make_unique<CameraHost>();
        host->worker=std::thread([ptr=host.get(),port]{ptr->Run(port);});
        {std::unique_lock guard(host->mutex);host->changed.wait(guard,[&]{return host->ready;});}
        if(FAILED(host->result))return host->result;
        *result=host.release();return S_OK;
    }catch(...){return winrt::to_hresult();}
}
extern "C" void __stdcall PhoneBoxCameraStop(void* handle) noexcept {delete static_cast<CameraHost*>(handle);}

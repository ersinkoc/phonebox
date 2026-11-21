#include "Camera.h"
#include "Attributes.h"

static HRESULT StreamAttributes(IMFAttributes* attributes) {
    CHECK_HR(attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY,PINNAME_VIDEO_CAPTURE));
    CHECK_HR(attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID,0));
    CHECK_HR(attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED,TRUE));
    return attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,MFFrameSourceTypes_Color);
}

class MediaStream : public winrt::implements<MediaStream,IMFMediaStream2> {
    Events events;
    std::mutex mutex;
    std::condition_variable wake;
    ComPtr<IMFMediaSource> parent;
    ComPtr<IMFStreamDescriptor> descriptor;
    ComPtr<IMFMediaType> mediaType;
    ComPtr<IMFVideoSampleAllocator> allocator;
    std::deque<ComPtr<IUnknown>> tokens;
    std::thread worker;
    UINT32 port=8765;
    bool shutdown=false;
    MF_STREAM_STATE state=MF_STREAM_STATE_STOPPED;
    uint64_t generation=0;

    HRESULT Sample(const std::vector<BYTE>& frame,IUnknown* token,LONGLONG time,IMFSample** output) {
        if(frame.size()!=FrameBytes)return E_FAIL;
        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buffer;
        CHECK_HR(allocator->AllocateSample(&sample));
        CHECK_HR(sample->GetBufferByIndex(0,&buffer));
        ComPtr<IMF2DBuffer> surface;
        CHECK_HR(buffer.As(&surface));
        BYTE* data=nullptr;
        LONG pitch=0;
        CHECK_HR(surface->Lock2D(&data,&pitch));
        if(pitch<static_cast<LONG>(Width)){surface->Unlock2D();return E_UNEXPECTED;}
        for(UINT32 y=0;y<Height+Height/2;++y)memcpy(data+size_t(y)*pitch,frame.data()+size_t(y)*Width,Width);
        CHECK_HR(surface->Unlock2D());
        CHECK_HR(buffer->SetCurrentLength(FrameBytes));
        CHECK_HR(sample->SetSampleTime(time));
        CHECK_HR(sample->SetSampleDuration(FrameDuration));
        if(token)CHECK_HR(sample->SetUnknown(MFSampleExtension_Token,token));
        else sample->DeleteItem(MFSampleExtension_Token);
        return sample.CopyTo(output);
    }

    void Run() noexcept {
        const HRESULT apartment=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        auto next=std::chrono::steady_clock::now();
        try {
            for(;;) {
                ComPtr<IUnknown> token;
                uint64_t gen;
                {
                    std::unique_lock guard(mutex);
                    wake.wait(guard,[&]{return shutdown || (state==MF_STREAM_STATE_RUNNING && !tokens.empty());});
                    if(shutdown)break;
                    gen=generation;
                    wake.wait_until(guard,next,[&]{return shutdown || generation!=gen || state!=MF_STREAM_STATE_RUNNING;});
                    if(shutdown)break;
                    if(gen!=generation || state!=MF_STREAM_STATE_RUNNING)continue;
                    token=tokens.front();tokens.pop_front();
                }
                std::vector<BYTE> frame;
                ReadFrame(port,frame);
                ComPtr<IMFSample> sample;
                // Capture timestamps use the system's MF clock; relative-to-start times are discarded by Frame Server.
                HRESULT hr=Sample(frame,token.Get(),MFGetSystemTime(),&sample);
                {
                    std::lock_guard guard(mutex);
                    if(shutdown || generation!=gen || state!=MF_STREAM_STATE_RUNNING)continue;
                    if(SUCCEEDED(hr))events.Object(MEMediaSample,sample.Get());
                    else events.Put(MEError,GUID_NULL,hr,nullptr);
                }
                next=std::chrono::steady_clock::now()+std::chrono::milliseconds(1000/Fps);
            }
        }catch(...) { events.Put(MEError,GUID_NULL,winrt::to_hresult(),nullptr); }
        if(SUCCEEDED(apartment))CoUninitialize();
    }
public:
    ~MediaStream(){Shutdown();}
    HRESULT Init(IMFMediaSource* source,UINT32 value) {
        port=value;
        CHECK_HR(events.Init());
        ComPtr<IMFMediaType> type;
        CHECK_HR(MFCreateMediaType(&type));
        CHECK_HR(type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));
        CHECK_HR(type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12));
        CHECK_HR(type->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
        CHECK_HR(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT,TRUE));
        CHECK_HR(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES,TRUE));
        CHECK_HR(type->SetUINT32(MF_MT_SAMPLE_SIZE,FrameBytes));
        CHECK_HR(type->SetUINT32(MF_MT_DEFAULT_STRIDE,Width));
        CHECK_HR(type->SetUINT32(MF_MT_AVG_BITRATE,FrameBytes*8*Fps));
        CHECK_HR(MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,Width,Height));
        CHECK_HR(MFSetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,Fps,1));
        CHECK_HR(MFSetAttributeRatio(type.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1));
        IMFMediaType* types[]={type.Get()};
        CHECK_HR(MFCreateStreamDescriptor(0,1,types,&descriptor));
        CHECK_HR(StreamAttributes(descriptor.Get()));
        ComPtr<IMFMediaTypeHandler> handler;
        CHECK_HR(descriptor->GetMediaTypeHandler(&handler));
        CHECK_HR(handler->SetCurrentMediaType(type.Get()));
        mediaType=type;
        parent=source;
        return S_OK;
    }
    EVENT_METHODS
    HRESULT STDMETHODCALLTYPE GetMediaSource(IMFMediaSource** source) override {
        std::lock_guard guard(mutex);return shutdown ? MF_E_SHUTDOWN : parent.CopyTo(source);
    }
    HRESULT STDMETHODCALLTYPE GetStreamDescriptor(IMFStreamDescriptor** stream) override {
        std::lock_guard guard(mutex);return shutdown ? MF_E_SHUTDOWN : descriptor.CopyTo(stream);
    }
    HRESULT STDMETHODCALLTYPE RequestSample(IUnknown* token) override try {
        std::lock_guard guard(mutex);
        if(shutdown)return MF_E_SHUTDOWN;
        if(state!=MF_STREAM_STATE_RUNNING)return MF_E_INVALIDREQUEST;
        if(tokens.size()>=8)return MF_E_NOTACCEPTING;
        tokens.emplace_back(token);wake.notify_all();return S_OK;
    }catch(...){return winrt::to_hresult();}
    HRESULT STDMETHODCALLTYPE GetStreamState(MF_STREAM_STATE* value) override {
        if(!value)return E_POINTER;
        std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;*value=state;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetStreamState(MF_STREAM_STATE value) override try {
        std::lock_guard guard(mutex);
        if(shutdown)return MF_E_SHUTDOWN;
        if(value!=MF_STREAM_STATE_RUNNING && value!=MF_STREAM_STATE_PAUSED && value!=MF_STREAM_STATE_STOPPED)return MF_E_INVALID_STATE_TRANSITION;
        if(value!=state) {
            ++generation;tokens.clear();state=value;
            if(value==MF_STREAM_STATE_RUNNING){
                if(!allocator){CHECK_HR(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator)));CHECK_HR(allocator->InitializeSampleAllocator(8,mediaType.Get()));}
                if(!worker.joinable())worker=std::thread([this]{Run();});
            }
            wake.notify_all();
        }
        return S_OK;
    }catch(...){return winrt::to_hresult();}
    HRESULT Start() {
        CHECK_HR(SetStreamState(MF_STREAM_STATE_RUNNING));
        PROPVARIANT position{};position.vt=VT_I8;position.hVal.QuadPart=0;
        return events.Put(MEStreamStarted,GUID_NULL,S_OK,&position);
    }
    HRESULT Stop() { CHECK_HR(SetStreamState(MF_STREAM_STATE_STOPPED));return events.Put(MEStreamStopped,GUID_NULL,S_OK,nullptr); }
    void Shutdown() {
        { std::lock_guard guard(mutex);shutdown=true;++generation;tokens.clear();state=MF_STREAM_STATE_STOPPED;wake.notify_all(); }
        events.Shutdown();
        if(worker.joinable())worker.join();
        if(allocator){allocator->UninitializeSampleAllocator();allocator.Reset();}
        parent.Reset();
    }
};

class MediaSource : public winrt::implements<MediaSource,IMFMediaSourceEx,IMFGetService,IKsControl,IMFSampleAllocatorControl> {
    Events events;
    std::mutex mutex;
    bool shutdown=false,announced=false;
    winrt::com_ptr<MediaStream> stream;
    ComPtr<IMFPresentationDescriptor> presentation;
    ComPtr<IMFAttributes> attributes;
public:
    ~MediaSource(){Shutdown();}
    HRESULT Init(IMFAttributes* activation) try {
        CHECK_HR(events.Init());
        CHECK_HR(MFCreateAttributes(&attributes,8));
        CHECK_HR(activation->CopyAllItems(attributes.Get()));
        UINT32 port=MFGetAttributeUINT32(activation,CameraPortAttribute,8765);
        if(port<1024 || port>65535)return E_INVALIDARG;
        stream=winrt::make_self<MediaStream>();
        CHECK_HR(stream->Init(this,port));
        ComPtr<IMFStreamDescriptor> descriptor;
        CHECK_HR(stream->GetStreamDescriptor(&descriptor));
        IMFStreamDescriptor* descriptors[]={descriptor.Get()};
        CHECK_HR(MFCreatePresentationDescriptor(1,descriptors,&presentation));
        return presentation->SelectStream(0);
    }catch(...){return winrt::to_hresult();}
    EVENT_METHODS
    HRESULT STDMETHODCALLTYPE GetCharacteristics(DWORD* value) override {
        if(!value)return E_POINTER;std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;*value=MFMEDIASOURCE_IS_LIVE;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE CreatePresentationDescriptor(IMFPresentationDescriptor** value) override {
        std::lock_guard guard(mutex);return shutdown ? MF_E_SHUTDOWN : presentation->Clone(value);
    }
    HRESULT STDMETHODCALLTYPE Start(IMFPresentationDescriptor* value,const GUID* format,const PROPVARIANT* position) override {
        if(!value || !position)return E_POINTER;
        if(format && *format!=GUID_NULL)return MF_E_UNSUPPORTED_TIME_FORMAT;
        if(position->vt!=VT_EMPTY && (position->vt!=VT_I8 || position->hVal.QuadPart!=0))return MF_E_INVALIDREQUEST;
        std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;
        DWORD count=0;CHECK_HR(value->GetStreamDescriptorCount(&count));if(count!=1)return E_INVALIDARG;
        BOOL selected=FALSE;ComPtr<IMFStreamDescriptor> descriptor;
        CHECK_HR(value->GetStreamDescriptorByIndex(0,&selected,&descriptor));
        DWORD id=~0u;CHECK_HR(descriptor->GetStreamIdentifier(&id));if(!selected || id!=0)return E_INVALIDARG;
        ComPtr<IMFMediaTypeHandler> handler;ComPtr<IMFMediaType> type;
        CHECK_HR(descriptor->GetMediaTypeHandler(&handler));CHECK_HR(handler->GetCurrentMediaType(&type));
        GUID subtype{};UINT32 w=0,h=0,numerator=0,denominator=0;
        CHECK_HR(type->GetGUID(MF_MT_SUBTYPE,&subtype));CHECK_HR(MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&w,&h));
        CHECK_HR(MFGetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,&numerator,&denominator));
        if(subtype!=MFVideoFormat_NV12 || w!=Width || h!=Height || numerator!=Fps || denominator!=1)return MF_E_INVALIDMEDIATYPE;
        CHECK_HR(events.Object(announced ? MEUpdatedStream : MENewStream,static_cast<IMFMediaStream*>(stream.get())));
        announced=true;
        CHECK_HR(stream->Start());
        PROPVARIANT time{};time.vt=VT_I8;
        return events.Put(MESourceStarted,GUID_NULL,S_OK,&time);
    }
    HRESULT STDMETHODCALLTYPE Stop() override {
        std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;
        CHECK_HR(stream->Stop());return events.Put(MESourceStopped,GUID_NULL,S_OK,nullptr);
    }
    HRESULT STDMETHODCALLTYPE Pause() override {return MF_E_INVALID_STATE_TRANSITION;}
    HRESULT STDMETHODCALLTYPE Shutdown() override {
        winrt::com_ptr<MediaStream> previous;
        { std::lock_guard guard(mutex);if(shutdown)return S_OK;shutdown=true;previous=std::move(stream); }
        events.Shutdown();if(previous)previous->Shutdown();return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSourceAttributes(IMFAttributes** value) override {
        std::lock_guard guard(mutex);return shutdown ? MF_E_SHUTDOWN : attributes.CopyTo(value);
    }
    HRESULT STDMETHODCALLTYPE GetStreamAttributes(DWORD id,IMFAttributes** value) override {
        std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;if(id!=0)return MF_E_INVALIDSTREAMNUMBER;
        ComPtr<IMFStreamDescriptor> descriptor;CHECK_HR(stream->GetStreamDescriptor(&descriptor));return descriptor->QueryInterface(IID_PPV_ARGS(value));
    }
    HRESULT STDMETHODCALLTYPE SetD3DManager(IUnknown*) override {std::lock_guard guard(mutex);return shutdown ? MF_E_SHUTDOWN : S_OK;}
    HRESULT STDMETHODCALLTYPE GetService(REFGUID,REFIID,void** value) override {if(!value)return E_POINTER;*value=nullptr;return MF_E_UNSUPPORTED_SERVICE;}
    HRESULT STDMETHODCALLTYPE KsProperty(PKSPROPERTY,ULONG,LPVOID,ULONG,ULONG* count) override {if(count)*count=0;return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);}
    HRESULT STDMETHODCALLTYPE KsMethod(PKSMETHOD,ULONG,LPVOID,ULONG,ULONG* count) override {if(count)*count=0;return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);}
    HRESULT STDMETHODCALLTYPE KsEvent(PKSEVENT,ULONG,LPVOID,ULONG,ULONG* count) override {if(count)*count=0;return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);}
    HRESULT STDMETHODCALLTYPE SetDefaultAllocator(DWORD id,IUnknown*) override {std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;return id==0 ? S_OK : MF_E_INVALIDSTREAMNUMBER;}
    HRESULT STDMETHODCALLTYPE GetAllocatorUsage(DWORD id,DWORD* input,MFSampleAllocatorUsage* usage) override {
        std::lock_guard guard(mutex);if(shutdown)return MF_E_SHUTDOWN;
        if(!input || !usage)return E_POINTER;if(id!=0)return MF_E_INVALIDSTREAMNUMBER;
        *input=0;*usage=MFSampleAllocatorUsage_UsesCustomAllocator;return S_OK;
    }
};

class Activate : public winrt::implements<Activate,IMFActivate> {
    std::mutex mutex;
    ComPtr<IMFAttributes> attributes;
    winrt::com_ptr<MediaSource> source;
public:
    Activate(){winrt::check_hresult(MFCreateAttributes(&attributes,8));}
    ATTRIBUTE_METHODS
    HRESULT STDMETHODCALLTYPE ActivateObject(REFIID iid,void** value) override try {
        if(!value)return E_POINTER;*value=nullptr;
        std::lock_guard guard(mutex);
        if(!source) {
            auto created=winrt::make_self<MediaSource>();
            HRESULT hr=created->Init(attributes.Get());
            if(FAILED(hr)){created->Shutdown();return hr;}
            source=std::move(created);
        }
        return source->QueryInterface(iid,value);
    }catch(...){return winrt::to_hresult();}
    HRESULT STDMETHODCALLTYPE ShutdownObject() override {
        std::lock_guard guard(mutex);if(source){source->Shutdown();source=nullptr;}return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DetachObject() override {std::lock_guard guard(mutex);source=nullptr;return S_OK;}
};

HRESULT CreateCameraActivate(REFIID iid,void** value) noexcept {
    if(!value)return E_POINTER;*value=nullptr;
    try{return winrt::make_self<Activate>()->QueryInterface(iid,value);}catch(...){return winrt::to_hresult();}
}

class Factory : public winrt::implements<Factory,IClassFactory> {
public:
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** value) override {return outer ? CLASS_E_NOAGGREGATION : CreateCameraActivate(iid,value);}
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {if(lock)++winrt::get_module_lock();else --winrt::get_module_lock();return S_OK;}
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid,REFIID iid,void** value) {
    if(!value)return E_POINTER;*value=nullptr;if(clsid!=CameraClassId)return CLASS_E_CLASSNOTAVAILABLE;
    try{return winrt::make_self<Factory>()->QueryInterface(iid,value);}catch(...){return winrt::to_hresult();}
}
extern "C" HRESULT __stdcall DllCanUnloadNow() {return winrt::get_module_lock() ? S_FALSE : S_OK;}

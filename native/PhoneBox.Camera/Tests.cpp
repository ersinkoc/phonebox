#include <winsock2.h>
#include "Camera.h"
#include <mfreadwrite.h>
#include <wincodec.h>
#include <iostream>
#include <stdexcept>

static void Require(bool result,const char* reason){if(!result)throw std::runtime_error(reason);}
static void Success(HRESULT hr,const char* reason){if(FAILED(hr)){std::cerr<<"HRESULT: "<<std::hex<<static_cast<unsigned long>(hr)<<"\n";throw std::runtime_error(reason);}}
static std::vector<BYTE> TestJpeg() {
    ComPtr<IWICImagingFactory> factory;Success(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)),"WIC factory");
    ComPtr<IStream> storage;Success(CreateStreamOnHGlobal(nullptr,TRUE,&storage),"memory stream");
    ComPtr<IWICBitmapEncoder> encoder;Success(factory->CreateEncoder(GUID_ContainerFormatJpeg,nullptr,&encoder),"JPEG encoder");
    Success(encoder->Initialize(storage.Get(),WICBitmapEncoderNoCache),"encoder initialize");
    ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IPropertyBag2> options;
    Success(encoder->CreateNewFrame(&frame,&options),"frame");Success(frame->Initialize(options.Get()),"frame initialize");
    Success(frame->SetSize(16,8),"size");GUID format=GUID_WICPixelFormat24bppBGR;Success(frame->SetPixelFormat(&format),"pixel format");
    std::vector<BYTE> pixels(16*8*3);
    for(size_t i=0;i<pixels.size();i+=3){pixels[i]=0;pixels[i+1]=0;pixels[i+2]=255;}
    Success(frame->WritePixels(8,16*3,static_cast<UINT>(pixels.size()),pixels.data()),"pixels");
    Success(frame->Commit(),"frame commit");Success(encoder->Commit(),"encoder commit");
    STATSTG info{};Success(storage->Stat(&info,STATFLAG_NONAME),"stat");
    LARGE_INTEGER zero{};Success(storage->Seek(zero,STREAM_SEEK_SET,nullptr),"seek");
    std::vector<BYTE> jpeg(static_cast<size_t>(info.cbSize.QuadPart));ULONG used=0;
    Success(storage->Read(jpeg.data(),static_cast<ULONG>(jpeg.size()),&used),"read JPEG");return jpeg;
}

static void Drain(IMFMediaEventGenerator* generator) {
    for(;;){ComPtr<IMFMediaEvent> event;HRESULT hr=generator->GetEvent(MF_EVENT_FLAG_NO_WAIT,&event);if(hr==MF_E_NO_EVENTS_AVAILABLE)return;Success(hr,"drain events");}
}
static void SourceTest(HMODULE dll) {
    auto factoryFunction=reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));
    Require(factoryFunction!=nullptr,"COM export");
    ComPtr<IClassFactory> factory;Success(factoryFunction(CameraClassId,IID_PPV_ARGS(&factory)),"class factory");
    ComPtr<IMFActivate> activate;Success(factory->CreateInstance(nullptr,IID_PPV_ARGS(&activate)),"activation");
    // A non-listening loopback port exercises the blank-frame path without an agent or phone.
    Success(activate->SetUINT32(CameraPortAttribute,65534),"destination attribute");
    ComPtr<IMFMediaSourceEx> source;Success(activate->ActivateObject(IID_PPV_ARGS(&source)),"source");
    activate.Reset(); // Frame Server can release the activation object while retaining its source.
    DWORD characteristics=0;Success(source->GetCharacteristics(&characteristics),"characteristics");Require(characteristics&MFMEDIASOURCE_IS_LIVE,"live source");
    ComPtr<IMFPresentationDescriptor> presentation;Success(source->CreatePresentationDescriptor(&presentation),"presentation");
    PROPVARIANT position{};position.vt=VT_EMPTY;
    Success(source->Start(presentation.Get(),nullptr,&position),"start");
    ComPtr<IMFMediaStream> stream;
    for(int i=0;i<4 && !stream;++i) {
        ComPtr<IMFMediaEvent> event;Success(source->GetEvent(0,&event),"source event");MediaEventType type{};Success(event->GetType(&type),"event type");
        if(type==MENewStream){PROPVARIANT value{};Success(event->GetValue(&value),"stream event value");Success(value.punkVal->QueryInterface(IID_PPV_ARGS(&stream)),"stream interface");PropVariantClear(&value);}
    }
    Require(stream!=nullptr,"stream announced");Drain(stream.Get());
    ComPtr<IMFAttributes> token;Success(MFCreateAttributes(&token,1),"sample token");
    Success(stream->RequestSample(token.Get()),"request sample");
    ComPtr<IMFMediaEvent> event;
    const auto deadline=GetTickCount64()+5000;
    for(;;){HRESULT hr=stream->GetEvent(MF_EVENT_FLAG_NO_WAIT,&event);if(hr!=MF_E_NO_EVENTS_AVAILABLE){Success(hr,"sample event");break;}Require(GetTickCount64()<deadline,"sample timeout");Sleep(10);}
    MediaEventType type{};Success(event->GetType(&type),"sample event type");Require(type==MEMediaSample,"sample delivery");
    PROPVARIANT value{};Success(event->GetValue(&value),"sample value");ComPtr<IMFSample> sample;Success(value.punkVal->QueryInterface(IID_PPV_ARGS(&sample)),"sample QI");PropVariantClear(&value);
    ComPtr<IMFMediaBuffer> buffer;Success(sample->ConvertToContiguousBuffer(&buffer),"sample buffer");DWORD length=0;Success(buffer->GetCurrentLength(&length),"buffer length");Require(length==FrameBytes,"NV12 frame size");
    ComPtr<IUnknown> returnedToken;Success(sample->GetUnknown(MFSampleExtension_Token,IID_PPV_ARGS(&returnedToken)),"token delivery");Require(returnedToken.Get()==token.Get(),"token identity");
    LONGLONG time=0;Success(sample->GetSampleTime(&time),"capture timestamp");Require(time>MFGetSystemTime()-50'000'000,"capture uses system MF clock");
    Success(source->Stop(),"stop");Require(FAILED(stream->RequestSample(nullptr)),"stopped stream rejects samples");
    Success(source->Shutdown(),"shutdown");Require(source->GetCharacteristics(&characteristics)==MF_E_SHUTDOWN,"shutdown state");
}

// A loopback-only test feed checks the real Windows Frame Server path without opening any physical camera.
class Fixture {
    SOCKET listener=INVALID_SOCKET;
    std::thread worker;
    std::atomic<bool> running{true};
    std::vector<BYTE> jpeg;
public:
    std::atomic<bool> video{true};
    UINT32 port=0;
    Fixture():jpeg(TestJpeg()) {
        WSADATA data{};Require(WSAStartup(MAKEWORD(2,2),&data)==0,"Winsock");
        listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);Require(listener!=INVALID_SOCKET,"fixture socket");
        sockaddr_in address{};address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
        Require(bind(listener,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0,"fixture bind");
        int size=sizeof(address);Require(getsockname(listener,reinterpret_cast<sockaddr*>(&address),&size)==0,"fixture port");
        port=ntohs(address.sin_port);Require(listen(listener,4)==0,"fixture listen");
        worker=std::thread([this]{
            while(running) {
                fd_set set;FD_ZERO(&set);FD_SET(listener,&set);timeval timeout{0,100000};
                if(select(0,&set,nullptr,nullptr,&timeout)<=0)continue;
                SOCKET client=accept(listener,nullptr,nullptr);if(client==INVALID_SOCKET)continue;
                DWORD wait=500;setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&wait),sizeof(wait));
                char request[8192];recv(client,request,sizeof(request),0);
                const bool live=video;
                std::string header=live ? "HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nX-Camera-Rotation: 0\r\nContent-Length: "+std::to_string(jpeg.size())+"\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n";
                send(client,header.data(),static_cast<int>(header.size()),0);
                if(live){size_t sent=0;while(sent<jpeg.size()){int n=send(client,reinterpret_cast<const char*>(jpeg.data()+sent),static_cast<int>(jpeg.size()-sent),0);if(n<=0)break;sent+=n;}}
                closesocket(client);
            }
        });
    }
    ~Fixture(){running=false;if(worker.joinable())worker.join();closesocket(listener);WSACleanup();}
};

static int DeviceSample(IMFSourceReader* reader) {
    DWORD flags=0;LONGLONG timestamp=0;ComPtr<IMFSample> sample;
    Success(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),0,nullptr,&flags,&timestamp,&sample),"Windows Frame Server sample");
    Require(!(flags & (MF_SOURCE_READERF_ERROR|MF_SOURCE_READERF_ENDOFSTREAM)),"live device stream");
    if(!sample)return -1; // initial media-type changes and stream ticks need not carry a sample
    ComPtr<IMFMediaBuffer> buffer;
    Success(sample->ConvertToContiguousBuffer(&buffer),"device buffer");BYTE* bytes=nullptr;DWORD size=0;
    Success(buffer->Lock(&bytes,nullptr,&size),"device pixels");Require(size==FrameBytes,"device NV12 size");
    BYTE center=bytes[Width*(Height/2)+Width/2];buffer->Unlock();return center;
}

static void DeviceTest(HMODULE dll) {
    Fixture fixture;
    auto start=reinterpret_cast<HRESULT(__stdcall*)(UINT32,void**)>(GetProcAddress(dll,"PhoneBoxCameraStart"));
    auto stop=reinterpret_cast<void(__stdcall*)(void*)>(GetProcAddress(dll,"PhoneBoxCameraStop"));
    Require(start && stop,"host exports");
    void* host=nullptr;Success(start(fixture.port,&host),"register session camera");
    std::cout<<"Session camera enabled\n"<<std::flush;
    try {
        ComPtr<IMFAttributes> attributes;Success(MFCreateAttributes(&attributes,2),"enumeration attributes");
        Success(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID),"video enumeration");
        ComPtr<IMFMediaSource> source;
        for(int attempt=0;attempt<30 && !source;++attempt) {
            IMFActivate** devices=nullptr;UINT32 count=0;Success(MFEnumDeviceSources(attributes.Get(),&devices,&count),"device enumeration");
            for(UINT32 i=0;i<count;++i) {
                wchar_t name[256]{};devices[i]->GetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,name,256,nullptr);
                if(wcsstr(name,L"PhoneBox Camera")) {
                    std::wcout<<L"Found: "<<name<<L"\n"<<std::flush;
                    HRESULT hr=devices[i]->ActivateObject(IID_PPV_ARGS(&source));
                    for(UINT32 j=0;j<count;++j)devices[j]->Release();CoTaskMemFree(devices);
                    Success(hr,"activate registered device");devices=nullptr;break;
                }
            }
            if(devices){for(UINT32 i=0;i<count;++i)devices[i]->Release();CoTaskMemFree(devices);}
            if(!source)Sleep(100);
        }
        Require(source!=nullptr,"camera appears in Windows enumeration");
        ComPtr<IMFSourceReader> reader;
        Success(MFCreateSourceReaderFromMediaSource(source.Get(),nullptr,&reader),"Windows device source reader");
        bool picture=false;for(int i=0;i<20 && !picture;++i)picture=DeviceSample(reader.Get())>60;
        Require(picture,"real device delivers loopback picture");
        std::cout<<"Frame Server delivered picture\n"<<std::flush;
        fixture.video=false;
        bool blank=false;for(int i=0;i<30 && !blank;++i)blank=DeviceSample(reader.Get())==16;
        Require(blank,"real device clears previous frame");
        reader.Reset();source->Shutdown();source.Reset();
        std::cout<<"Frame Server cleared picture\n"<<std::flush;
    }catch(...){stop(host);throw;}
    stop(host);
}

int wmain(int argc,wchar_t** argv) {
    std::jthread watchdog([](std::stop_token cancellation){for(int i=0;i<600;++i){if(cancellation.stop_requested())return;Sleep(50);}std::cerr<<"FAIL: integration test timed out\n";TerminateProcess(GetCurrentProcess(),124);});
    try {
        Require(argc==2 || (argc==3 && !wcscmp(argv[2],L"--device")),"DLL argument");Success(CoInitializeEx(nullptr,COINIT_MULTITHREADED),"COM");Success(MFStartup(MF_VERSION),"MF startup");
        std::vector<BYTE> output;auto jpeg=TestJpeg();
        for(int angle : {0,90,180,270}) {
            Success(DecodeFrame(jpeg,angle,output),"decode and rotate");Require(output.size()==FrameBytes,"frame bounds");
            Require(output[Width*(Height/2)+Width/2]>60,"center picture");
            if(angle%180)Require(output[Width*(Height/2)]==16,"portrait letterbox");
        }
        Require(FAILED(DecodeFrame({1,2,3,4},0,output)),"malformed JPEG rejected");Require(output[Width*(Height/2)+Width/2]==16,"failed decode clears image");
        Require(FAILED(DecodeFrame(jpeg,45,output)),"invalid rotation rejected");
        {uint64_t sequence=7;ReadFrame(0,output,sequence);Require(sequence==0 && output.size()==FrameBytes && output[0]==16 && output[Width*Height]==128,"disconnected source black");}
        HMODULE dll=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);Require(dll!=nullptr,"load source DLL");
        SourceTest(dll);
        if(argc==3)DeviceTest(dll);
        FreeLibrary(dll);
        MFShutdown();CoUninitialize();std::cout<<"PASS: JPEG rotation, NV12, letterboxing, failure clearing, COM activation, samples, tokens and lifecycle\n";return 0;
    }catch(const std::exception& error){std::cerr<<"FAIL: "<<error.what()<<"\n";return 1;}
}

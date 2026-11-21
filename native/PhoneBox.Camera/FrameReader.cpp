#include "Camera.h"
#include <wincodec.h>
#include <winhttp.h>
#include <memory>

void BlankFrame(std::vector<BYTE>& frame) {
    frame.assign(FrameBytes, 128);
    std::fill_n(frame.begin(), Width*Height, BYTE(16)); // limited-range NV12 black
}

HRESULT DecodeFrame(const std::vector<BYTE>& jpeg, int rotation, std::vector<BYTE>& frame) try {
    BlankFrame(frame);
    if (jpeg.size()<4 || jpeg.size()>MaxJpeg || (rotation!=0 && rotation!=90 && rotation!=180 && rotation!=270)) return E_INVALIDARG;
    ComPtr<IWICImagingFactory> factory;
    CHECK_HR(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)));
    ComPtr<IWICStream> input;
    CHECK_HR(factory->CreateStream(&input));
    CHECK_HR(input->InitializeFromMemory(const_cast<BYTE*>(jpeg.data()),static_cast<DWORD>(jpeg.size())));
    ComPtr<IWICBitmapDecoder> decoder;
    CHECK_HR(factory->CreateDecoderFromStream(input.Get(),nullptr,WICDecodeMetadataCacheOnDemand,&decoder));
    ComPtr<IWICBitmapFrameDecode> image;
    CHECK_HR(decoder->GetFrame(0,&image));
    UINT w=0,h=0;
    CHECK_HR(image->GetSize(&w,&h));
    if (!w || !h || w>1280 || h>1280 || uint64_t(w)*h>Width*Height) return E_INVALIDARG;
    ComPtr<IWICFormatConverter> converter;
    CHECK_HR(factory->CreateFormatConverter(&converter));
    CHECK_HR(converter->Initialize(image.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
    std::vector<BYTE> pixels(size_t(w)*h*4);
    CHECK_HR(converter->CopyPixels(nullptr,w*4,static_cast<UINT>(pixels.size()),pixels.data()));
    const UINT rw=rotation%180 ? h : w, rh=rotation%180 ? w : h;
    const double scale=std::min(double(Width)/rw,double(Height)/rh);
    const UINT dw=std::max(2u,static_cast<UINT>(rw*scale)&~1u), dh=std::max(2u,static_cast<UINT>(rh*scale)&~1u);
    const UINT left=((Width-dw)/2)&~1u, top=((Height-dh)/2)&~1u;
    auto pixel=[&](UINT x,UINT y) {
        const UINT rx=std::min(rw-1,x*rw/dw), ry=std::min(rh-1,y*rh/dh);
        UINT sx=rx,sy=ry;
        if(rotation==90){sx=ry;sy=h-1-rx;}
        else if(rotation==180){sx=w-1-rx;sy=h-1-ry;}
        else if(rotation==270){sx=w-1-ry;sy=rx;}
        return pixels.data()+(size_t(sy)*w+sx)*4;
    };
    auto clamp=[](int value){return static_cast<BYTE>(std::clamp(value,0,255));};
    for(UINT y=0;y<dh;++y) for(UINT x=0;x<dw;++x) {
        auto p=pixel(x,y); const int b=p[0],g=p[1],r=p[2];
        frame[size_t(top+y)*Width+left+x]=clamp(((66*r+129*g+25*b+128)>>8)+16);
    }
    for(UINT y=0;y<dh;y+=2) for(UINT x=0;x<dw;x+=2) {
        int r=0,g=0,b=0;
        for(UINT dy=0;dy<2;++dy) for(UINT dx=0;dx<2;++dx){auto p=pixel(x+dx,y+dy);b+=p[0];g+=p[1];r+=p[2];}
        r/=4;g/=4;b/=4;
        auto index=Width*Height+size_t((top+y)/2)*Width+left+x;
        frame[index]=clamp(((-38*r-74*g+112*b+128)>>8)+128);
        frame[index+1]=clamp(((112*r-94*g-18*b+128)>>8)+128);
    }
    return S_OK;
} catch(...) { return winrt::to_hresult(); }

struct HttpCloser { void operator()(void* handle) const { if(handle)WinHttpCloseHandle(handle); } };
using Http = std::unique_ptr<void,HttpCloser>;

void ReadFrame(UINT32 port,std::vector<BYTE>& frame) noexcept {
    try {
        BlankFrame(frame);
        if(port<1024 || port>65535) return;
        Http session(WinHttpOpen(L"PhoneBox Camera/1",WINHTTP_ACCESS_TYPE_NO_PROXY,nullptr,nullptr,0));
        if(!session) return;
        WinHttpSetTimeouts(session.get(),100,200,300,500);
        Http connection(WinHttpConnect(session.get(),L"127.0.0.1",static_cast<INTERNET_PORT>(port),0));
        if(!connection)return;
        Http request(WinHttpOpenRequest(connection.get(),L"GET",L"/api/phone-camera/frame",nullptr,nullptr,WINHTTP_DEFAULT_ACCEPT_TYPES,0));
        if(!request)return;
        DWORD redirect=WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
        if(!WinHttpSetOption(request.get(),WINHTTP_OPTION_REDIRECT_POLICY,&redirect,sizeof(redirect)))return;
        if(!WinHttpSendRequest(request.get(),L"Cache-Control: no-cache\r\n",DWORD(-1),nullptr,0,0,0) || !WinHttpReceiveResponse(request.get(),nullptr))return;
        DWORD status=0,size=sizeof(status);
        if(!WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,nullptr,&status,&size,nullptr) || status!=200)return;
        wchar_t angle[16]{};size=sizeof(angle);
        if(!WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_CUSTOM,L"X-Camera-Rotation",angle,&size,nullptr))return;
        wchar_t* end=nullptr;long rotation=wcstol(angle,&end,10);
        if(!end || *end || (rotation!=0 && rotation!=90 && rotation!=180 && rotation!=270))return;
        std::vector<BYTE> jpeg;
        BYTE chunk[16384]; DWORD count=0;
        const auto deadline=GetTickCount64()+1000;
        do {
            if(GetTickCount64()>deadline || !WinHttpReadData(request.get(),chunk,sizeof(chunk),&count))return;
            if(jpeg.size()+count>MaxJpeg)return;
            jpeg.insert(jpeg.end(),chunk,chunk+count);
        }while(count);
        if(FAILED(DecodeFrame(jpeg,static_cast<int>(rotation),frame)))BlankFrame(frame);
    } catch(...) {
        // Never retain a previous image after a timeout, malformed frame or disconnected agent.
        try { BlankFrame(frame); } catch(...) { frame.clear(); }
    }
}

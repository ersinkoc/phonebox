#include "Camera.h"
#include <wincodec.h>
#include <winhttp.h>
#include <memory>
#include <string>

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
    if (!w || !h || w>1920 || h>1920 || uint64_t(w)*h>Width*Height) return E_INVALIDARG;
    ComPtr<IWICFormatConverter> converter;
    CHECK_HR(factory->CreateFormatConverter(&converter));
    CHECK_HR(converter->Initialize(image.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
    std::vector<BYTE> pixels(size_t(w)*h*4);
    CHECK_HR(converter->CopyPixels(nullptr,w*4,static_cast<UINT>(pixels.size()),pixels.data()));
    const UINT rw=rotation%180 ? h : w, rh=rotation%180 ? w : h;
    const double scale=std::min(double(Width)/rw,double(Height)/rh);
    const UINT dw=std::clamp(static_cast<UINT>(rw*scale)&~1u,2u,Width), dh=std::clamp(static_cast<UINT>(rh*scale)&~1u,2u,Height);
    const UINT left=((Width-dw)/2)&~1u, top=((Height-dh)/2)&~1u;
    // Nearest-neighbour scaling and rotation, separated per axis: the source pixel of (x,y) is row[y]+column[x].
    // A division per pixel at 1920 x 1080, 30 times a second, was most of the frame time.
    std::vector<size_t> row(dh), column(dw);
    for(UINT y=0;y<dh;++y) {
        const UINT ry=std::min(rh-1,y*rh/dh);
        row[y]=rotation==0 ? size_t(ry)*w : rotation==180 ? size_t(h-1-ry)*w : rotation==90 ? size_t(ry) : size_t(w-1-ry);
    }
    for(UINT x=0;x<dw;++x) {
        const UINT rx=std::min(rw-1,x*rw/dw);
        column[x]=rotation==0 ? size_t(rx) : rotation==180 ? size_t(w-1-rx) : rotation==90 ? size_t(h-1-rx)*w : size_t(rx)*w;
    }
    const BYTE* source=pixels.data();
    auto clamp=[](int value){return static_cast<BYTE>(std::clamp(value,0,255));};
    for(UINT y=0;y<dh;++y) {
        BYTE* out=frame.data()+size_t(top+y)*Width+left;
        const size_t base=row[y];
        for(UINT x=0;x<dw;++x) {
            const BYTE* p=source+(base+column[x])*4;
            out[x]=static_cast<BYTE>(((66*p[2]+129*p[1]+25*p[0]+128)>>8)+16);
        }
    }
    for(UINT y=0;y<dh;y+=2) {
        BYTE* out=frame.data()+size_t(Width)*Height+size_t((top+y)/2)*Width+left;
        const size_t ra=row[y], rb=row[y+1];
        for(UINT x=0;x<dw;x+=2) {
            const size_t ca=column[x], cb=column[x+1];
            const BYTE *p0=source+(ra+ca)*4,*p1=source+(ra+cb)*4,*p2=source+(rb+ca)*4,*p3=source+(rb+cb)*4;
            const int bl=(p0[0]+p1[0]+p2[0]+p3[0])/4, g=(p0[1]+p1[1]+p2[1]+p3[1])/4, r=(p0[2]+p1[2]+p2[2]+p3[2])/4;
            out[x]=clamp(((-38*r-74*g+112*bl+128)>>8)+128);
            out[x+1]=clamp(((112*r-94*g-18*bl+128)>>8)+128);
        }
    }
    return S_OK;
} catch(...) { return winrt::to_hresult(); }

struct HttpCloser { void operator()(void* handle) const { if(handle)WinHttpCloseHandle(handle); } };
using Http = std::unique_ptr<void,HttpCloser>;

void ReadFrame(UINT32 port,std::vector<BYTE>& frame,uint64_t& sequence) noexcept {
    const uint64_t known=frame.size()==FrameBytes ? sequence : 0;
    sequence=0;
    try {
        if(port<1024 || port>65535) { BlankFrame(frame); return; }
        Http session(WinHttpOpen(L"PhoneBox Camera/1",WINHTTP_ACCESS_TYPE_NO_PROXY,nullptr,nullptr,0));
        if(!session) return;
        WinHttpSetTimeouts(session.get(),100,200,300,500);
        Http connection(WinHttpConnect(session.get(),L"127.0.0.1",static_cast<INTERNET_PORT>(port),0));
        if(!connection)return;
        Http request(WinHttpOpenRequest(connection.get(),L"GET",L"/api/phone-camera/frame",nullptr,nullptr,WINHTTP_DEFAULT_ACCEPT_TYPES,0));
        if(!request)return;
        DWORD redirect=WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
        if(!WinHttpSetOption(request.get(),WINHTTP_OPTION_REDIRECT_POLICY,&redirect,sizeof(redirect)))return;
        const std::wstring headers=L"Cache-Control: no-cache\r\nX-Camera-Known: "+std::to_wstring(known)+L"\r\n";
        if(!WinHttpSendRequest(request.get(),headers.c_str(),DWORD(-1),nullptr,0,0,0) || !WinHttpReceiveResponse(request.get(),nullptr)) { BlankFrame(frame); return; }
        DWORD status=0,size=sizeof(status);
        if(!WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,nullptr,&status,&size,nullptr)) { BlankFrame(frame); return; }
        uint64_t current=0;
        wchar_t number[32]{};size=sizeof(number);
        if(WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_CUSTOM,L"X-Camera-Frame",number,&size,nullptr)) {
            wchar_t* tail=nullptr;current=wcstoull(number,&tail,10);
            if(!tail || *tail)current=0;
        }
        // Unchanged since the last read: keep the decoded picture. The agent answers 304 only while it still holds it.
        if(status==304 && known && current==known) { sequence=known; return; }
        BlankFrame(frame);
        if(status!=200)return;
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
        else sequence=current;
    } catch(...) {
        sequence=0;
        // Never retain a previous image after a timeout, malformed frame or disconnected agent.
        try { BlankFrame(frame); } catch(...) { frame.clear(); }
    }
}

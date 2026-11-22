#pragma once
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksmedia.h>
#include <ksproxy.h>
#include <winrt/base.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
// Classic COM interface inheritance must also be advertised by QueryInterface.
namespace winrt {
    template<> inline bool is_guid_of<IMFMediaSourceEx>(guid const& id) noexcept {return is_guid_of<IMFMediaSourceEx,IMFMediaSource,IMFMediaEventGenerator>(id);}
    template<> inline bool is_guid_of<IMFMediaStream2>(guid const& id) noexcept {return is_guid_of<IMFMediaStream2,IMFMediaStream,IMFMediaEventGenerator>(id);}
    template<> inline bool is_guid_of<IMFActivate>(guid const& id) noexcept {return is_guid_of<IMFActivate,IMFAttributes>(id);}
}
inline constexpr GUID CameraClassId = {0x4f412bc4,0x5e9d,0x463c,{0xa8,0xca,0x6c,0x13,0x7a,0xf6,0x70,0x17}};
inline constexpr wchar_t CameraClsid[] = L"{4F412BC4-5E9D-463C-A8CA-6C137AF67017}";
inline constexpr GUID CameraPortAttribute = {0x7aff225c,0x411a,0x468d,{0x88,0x54,0x55,0x4d,0xb5,0x01,0x71,0xe0}};
inline constexpr UINT32 Width = 1920, Height = 1080, Fps = 30;
inline constexpr DWORD FrameBytes = Width * Height * 3 / 2;
inline constexpr LONGLONG FrameDuration = 10'000'000 / Fps;
inline constexpr DWORD MaxJpeg = 1536 * 1024; // matches the agent's PhoneCamera.MaxFrameBytes

#define CHECK_HR(expression) do { HRESULT checkResult = (expression); if (FAILED(checkResult)) return checkResult; } while (false)

// Each generator owns its event queue. Snapshot it before a potentially blocking GetEvent.
class Events {
    std::mutex lock;
    ComPtr<IMFMediaEventQueue> queue;
public:
    HRESULT Init() { return MFCreateEventQueue(&queue); }
    ComPtr<IMFMediaEventQueue> Snapshot() { std::lock_guard guard(lock); return queue; }
    HRESULT Begin(IMFAsyncCallback* cb, IUnknown* state) { auto q=Snapshot(); return q ? q->BeginGetEvent(cb,state) : MF_E_SHUTDOWN; }
    HRESULT End(IMFAsyncResult* r, IMFMediaEvent** e) { auto q=Snapshot(); return q ? q->EndGetEvent(r,e) : MF_E_SHUTDOWN; }
    HRESULT Get(DWORD flags, IMFMediaEvent** e) { auto q=Snapshot(); return q ? q->GetEvent(flags,e) : MF_E_SHUTDOWN; }
    HRESULT Put(MediaEventType type, REFGUID guid, HRESULT status, const PROPVARIANT* value) { auto q=Snapshot(); return q ? q->QueueEventParamVar(type,guid,status,value) : MF_E_SHUTDOWN; }
    HRESULT Object(MediaEventType type, IUnknown* value) { auto q=Snapshot(); return q ? q->QueueEventParamUnk(type,GUID_NULL,S_OK,value) : MF_E_SHUTDOWN; }
    void Shutdown() { ComPtr<IMFMediaEventQueue> q; { std::lock_guard guard(lock); q.Swap(queue); } if(q) q->Shutdown(); }
};
#define EVENT_METHODS \
 HRESULT STDMETHODCALLTYPE BeginGetEvent(IMFAsyncCallback* cb,IUnknown* callbackState) override { return events.Begin(cb,callbackState); } \
 HRESULT STDMETHODCALLTYPE EndGetEvent(IMFAsyncResult* r,IMFMediaEvent** e) override { return events.End(r,e); } \
 HRESULT STDMETHODCALLTYPE GetEvent(DWORD flags,IMFMediaEvent** e) override { return events.Get(flags,e); } \
 HRESULT STDMETHODCALLTYPE QueueEvent(MediaEventType type,REFGUID guid,HRESULT status,const PROPVARIANT* value) override { return events.Put(type,guid,status,value); }

void BlankFrame(std::vector<BYTE>& frame);
HRESULT DecodeFrame(const std::vector<BYTE>& jpeg, int rotation, std::vector<BYTE>& frame);
// Only fixed loopback HTTP is used. No phone keys, arbitrary URLs, proxy, redirect or disk access.
// sequence is the agent's number of the frame already decoded in `frame`. While the agent still holds that frame,
// `frame` is kept as is. Anything else (no frame, timeout, malformed) blanks it and resets sequence to 0.
void ReadFrame(UINT32 port, std::vector<BYTE>& frame, uint64_t& sequence) noexcept;
HRESULT CreateCameraActivate(REFIID iid, void** result) noexcept;

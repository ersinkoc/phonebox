#include "Camera.h"
#include <shlobj.h>
#include <filesystem>
#include <iostream>

static std::wstring ClassKey(){return std::wstring(L"SOFTWARE\\Classes\\CLSID\\")+CameraClsid;}
static HRESULT WriteString(HKEY parent,const std::wstring& path,const wchar_t* name,const std::wstring& value) {
    HKEY key=nullptr;
    LSTATUS status=RegCreateKeyExW(parent,path.c_str(),0,nullptr,0,KEY_WRITE|KEY_WOW64_64KEY,nullptr,&key,nullptr);
    if(status!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(status);
    status=RegSetValueExW(key,name,0,REG_SZ,reinterpret_cast<const BYTE*>(value.c_str()),static_cast<DWORD>((value.size()+1)*sizeof(wchar_t)));
    RegCloseKey(key);return HRESULT_FROM_WIN32(status);
}
static HRESULT Install(bool remove) try {
    PWSTR programFiles=nullptr;
    CHECK_HR(SHGetKnownFolderPath(FOLDERID_ProgramFiles,KF_FLAG_DEFAULT,nullptr,&programFiles));
    std::filesystem::path directory=std::filesystem::path(programFiles)/L"PhoneBox"/L"Camera";
    CoTaskMemFree(programFiles);
    const auto dll=directory/L"PhoneBoxCamera.dll";
    const auto helper=directory/L"PhoneBoxCameraSetup.exe";
    if(remove) {
        // Only our class is removed; there is no recursive filesystem deletion or service modification.
        HKEY classes=nullptr;
        auto status=RegOpenKeyExW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Classes\\CLSID",0,KEY_READ|KEY_WRITE|DELETE|KEY_WOW64_64KEY,&classes);
        if(status!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(status);
        status=RegDeleteTreeW(classes,CameraClsid);RegCloseKey(classes);
        if(status!=ERROR_SUCCESS && status!=ERROR_FILE_NOT_FOUND)return HRESULT_FROM_WIN32(status);
        DeleteFileW(dll.c_str()); // Frame Server can retain a loaded DLL until its clients close.
        return S_OK;
    }
    wchar_t executable[32768]{};
    if(!GetModuleFileNameW(nullptr,executable,32768))return HRESULT_FROM_WIN32(GetLastError());
    auto source=std::filesystem::path(executable).parent_path();
    if(!std::filesystem::is_regular_file(source/L"PhoneBoxCamera.dll"))return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    for(auto folder : {directory.parent_path(),directory}) {
        if(std::filesystem::exists(folder) && (GetFileAttributesW(folder.c_str())&FILE_ATTRIBUTE_REPARSE_POINT))return E_ACCESSDENIED;
    }
    std::filesystem::create_directories(directory);
    if(source!=directory) {
        if(!CopyFileW((source/L"PhoneBoxCamera.dll").c_str(),dll.c_str(),FALSE))return HRESULT_FROM_WIN32(GetLastError());
        if(!CopyFileW(executable,helper.c_str(),FALSE))return HRESULT_FROM_WIN32(GetLastError());
    }
    CHECK_HR(WriteString(HKEY_LOCAL_MACHINE,ClassKey(),nullptr,L"PhoneBox Camera media source"));
    CHECK_HR(WriteString(HKEY_LOCAL_MACHINE,ClassKey()+L"\\InprocServer32",nullptr,dll.wstring()));
    return WriteString(HKEY_LOCAL_MACHINE,ClassKey()+L"\\InprocServer32",L"ThreadingModel",L"Both");
}catch(const std::filesystem::filesystem_error& error){return HRESULT_FROM_WIN32(error.code().value());}
catch(...){return E_FAIL;}

int wmain(int argc,wchar_t** argv) {
    if(argc!=2 || (wcscmp(argv[1],L"--install") && wcscmp(argv[1],L"--uninstall"))) {
        std::wcerr<<L"Usage: PhoneBoxCameraSetup.exe --install | --uninstall (administrator)\n";return 2;
    }
    HRESULT hr=Install(!wcscmp(argv[1],L"--uninstall"));
    if(FAILED(hr)){std::wcerr<<L"PhoneBox Camera setup failed: 0x"<<std::hex<<static_cast<unsigned long>(hr)<<L"\n";return static_cast<int>(hr);}
    std::wcout<<L"PhoneBox Camera component updated.\n";return 0;
}

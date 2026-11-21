#pragma once
// IMFActivate inherits IMFAttributes; storage and synchronization belong to Media Foundation.
#define ATTRIBUTE_METHODS \
 HRESULT STDMETHODCALLTYPE GetItem(REFGUID k,PROPVARIANT* v) override{return attributes->GetItem(k,v);} \
 HRESULT STDMETHODCALLTYPE GetItemType(REFGUID k,MF_ATTRIBUTE_TYPE* v) override{return attributes->GetItemType(k,v);} \
 HRESULT STDMETHODCALLTYPE CompareItem(REFGUID k,REFPROPVARIANT v,BOOL* r) override{return attributes->CompareItem(k,v,r);} \
 HRESULT STDMETHODCALLTYPE Compare(IMFAttributes* a,MF_ATTRIBUTES_MATCH_TYPE t,BOOL* r) override{return attributes->Compare(a,t,r);} \
 HRESULT STDMETHODCALLTYPE GetUINT32(REFGUID k,UINT32* v) override{return attributes->GetUINT32(k,v);} \
 HRESULT STDMETHODCALLTYPE GetUINT64(REFGUID k,UINT64* v) override{return attributes->GetUINT64(k,v);} \
 HRESULT STDMETHODCALLTYPE GetDouble(REFGUID k,double* v) override{return attributes->GetDouble(k,v);} \
 HRESULT STDMETHODCALLTYPE GetGUID(REFGUID k,GUID* v) override{return attributes->GetGUID(k,v);} \
 HRESULT STDMETHODCALLTYPE GetStringLength(REFGUID k,UINT32* n) override{return attributes->GetStringLength(k,n);} \
 HRESULT STDMETHODCALLTYPE GetString(REFGUID k,LPWSTR s,UINT32 n,UINT32* used) override{return attributes->GetString(k,s,n,used);} \
 HRESULT STDMETHODCALLTYPE GetAllocatedString(REFGUID k,LPWSTR* s,UINT32* n) override{return attributes->GetAllocatedString(k,s,n);} \
 HRESULT STDMETHODCALLTYPE GetBlobSize(REFGUID k,UINT32* n) override{return attributes->GetBlobSize(k,n);} \
 HRESULT STDMETHODCALLTYPE GetBlob(REFGUID k,UINT8* b,UINT32 n,UINT32* used) override{return attributes->GetBlob(k,b,n,used);} \
 HRESULT STDMETHODCALLTYPE GetAllocatedBlob(REFGUID k,UINT8** b,UINT32* n) override{return attributes->GetAllocatedBlob(k,b,n);} \
 HRESULT STDMETHODCALLTYPE GetUnknown(REFGUID k,REFIID iid,void** v) override{return attributes->GetUnknown(k,iid,v);} \
 HRESULT STDMETHODCALLTYPE SetItem(REFGUID k,REFPROPVARIANT v) override{return attributes->SetItem(k,v);} \
 HRESULT STDMETHODCALLTYPE DeleteItem(REFGUID k) override{return attributes->DeleteItem(k);} \
 HRESULT STDMETHODCALLTYPE DeleteAllItems() override{return attributes->DeleteAllItems();} \
 HRESULT STDMETHODCALLTYPE SetUINT32(REFGUID k,UINT32 v) override{return attributes->SetUINT32(k,v);} \
 HRESULT STDMETHODCALLTYPE SetUINT64(REFGUID k,UINT64 v) override{return attributes->SetUINT64(k,v);} \
 HRESULT STDMETHODCALLTYPE SetDouble(REFGUID k,double v) override{return attributes->SetDouble(k,v);} \
 HRESULT STDMETHODCALLTYPE SetGUID(REFGUID k,REFGUID v) override{return attributes->SetGUID(k,v);} \
 HRESULT STDMETHODCALLTYPE SetString(REFGUID k,LPCWSTR v) override{return attributes->SetString(k,v);} \
 HRESULT STDMETHODCALLTYPE SetBlob(REFGUID k,const UINT8* b,UINT32 n) override{return attributes->SetBlob(k,b,n);} \
 HRESULT STDMETHODCALLTYPE SetUnknown(REFGUID k,IUnknown* v) override{return attributes->SetUnknown(k,v);} \
 HRESULT STDMETHODCALLTYPE LockStore() override{return attributes->LockStore();} \
 HRESULT STDMETHODCALLTYPE UnlockStore() override{return attributes->UnlockStore();} \
 HRESULT STDMETHODCALLTYPE GetCount(UINT32* n) override{return attributes->GetCount(n);} \
 HRESULT STDMETHODCALLTYPE GetItemByIndex(UINT32 n,GUID* k,PROPVARIANT* v) override{return attributes->GetItemByIndex(n,k,v);} \
 HRESULT STDMETHODCALLTYPE CopyAllItems(IMFAttributes* a) override{return attributes->CopyAllItems(a);}

// DROP 08 Phase 5 - the class factory COM asks for before it asks for anything else.

#pragma once

#include <unknwn.h>

namespace noba {

class ClassFactory : public IClassFactory {
 public:
    ClassFactory() = default;

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override;
    IFACEMETHODIMP LockServer(BOOL lock) override;

 private:
    virtual ~ClassFactory() = default;

    LONG references_ = 1;
};

// Objects and locks outstanding. DllCanUnloadNow answers from these, and answering wrongly unloads a
// DLL the shell still holds pointers into.
extern LONG g_objects;
extern LONG g_locks;

}  // namespace noba

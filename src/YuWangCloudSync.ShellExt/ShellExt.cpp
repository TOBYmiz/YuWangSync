#include <windows.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <cfapi.h>
#include <atlbase.h>
#include <atlcom.h>
#include <atlstr.h>
#include <cstring>
#include <cwchar>

constexpr wchar_t ClassId[] = L"{B7519EF2-1F39-4612-B5AB-FD70C37E905B}";
constexpr wchar_t OverlayDescription[] = PRODUCT_NAME_EN_W L" synced overlay";
constexpr wchar_t ProgId[] = PRODUCT_IDENTIFIER_W L".SyncStatusOverlay.1";
constexpr wchar_t OverlayKey[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\" PRODUCT_IDENTIFIER_W;
constexpr GUID ProviderId = {0xA75C7E2E, 0x1048, 0x4EF8, {0xA1, 0x1D, 0x6A, 0x92, 0xB7, 0x84, 0x0B, 0x23}};

class ShellModule final : public ATL::CAtlDllModuleT<ShellModule> {};
ShellModule Module;

class ATL_NO_VTABLE __declspec(uuid("B7519EF2-1F39-4612-B5AB-FD70C37E905B")) SyncStatusOverlay :
    public ATL::CComObjectRootEx<ATL::CComMultiThreadModel>,
    public ATL::CComCoClass<SyncStatusOverlay, &__uuidof(SyncStatusOverlay)>,
    public IShellIconOverlayIdentifier
{
public:
    DECLARE_NO_REGISTRY()
    DECLARE_NOT_AGGREGATABLE(SyncStatusOverlay)
    BEGIN_COM_MAP(SyncStatusOverlay)
        COM_INTERFACE_ENTRY(IShellIconOverlayIdentifier)
    END_COM_MAP()

    STDMETHOD(GetOverlayInfo)(LPWSTR iconFile, int capacity, int* iconIndex, DWORD* flags) override
    {
        if (iconFile == nullptr || iconIndex == nullptr || flags == nullptr || capacity <= 0) return E_INVALIDARG;
        const DWORD length = GetModuleFileNameW(ATL::_AtlBaseModule.GetModuleInstance(), iconFile, static_cast<DWORD>(capacity));
        if (length == 0) return HRESULT_FROM_WIN32(GetLastError());
        if (length >= static_cast<DWORD>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        *iconIndex = 0;
        *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
        return S_OK;
    }

    STDMETHOD(GetPriority)(int* priority) override
    {
        if (priority == nullptr) return E_POINTER;
        *priority = 80;
        return S_OK;
    }

    STDMETHOD(IsMemberOf)(LPCWSTR path, DWORD attributes) override
    {
        if (path == nullptr || *path == L'\0') return S_FALSE;
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0) return S_FALSE;
        if ((attributes & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0) return S_FALSE;
        if (path[0] == L'\\' && path[1] == L'\\' && _wcsnicmp(path, L"\\\\?\\", 4) != 0) return S_FALSE;
        if (_wcsnicmp(path, L"\\\\?\\UNC\\", 8) == 0) return S_FALSE;

        const HANDLE rawHandle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (rawHandle == INVALID_HANDLE_VALUE) return S_FALSE;
        ATL::CHandle handle(rawHandle);
        FILE_ATTRIBUTE_TAG_INFO tag{};
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &tag, sizeof(tag))) return S_FALSE;
        const auto state = CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes, tag.ReparseTag);
        if (state == CF_PLACEHOLDER_STATE_INVALID
            || (state & (CF_PLACEHOLDER_STATE_PLACEHOLDER | CF_PLACEHOLDER_STATE_IN_SYNC))
                != (CF_PLACEHOLDER_STATE_PLACEHOLDER | CF_PLACEHOLDER_STATE_IN_SYNC)
            || (state & (CF_PLACEHOLDER_STATE_PARTIAL | CF_PLACEHOLDER_STATE_PARTIALLY_ON_DISK)) != 0) return S_FALSE;
        if ((tag.FileAttributes & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0) return S_FALSE;
        alignas(CF_SYNC_ROOT_STANDARD_INFO) BYTE rootBuffer[sizeof(CF_SYNC_ROOT_STANDARD_INFO) + sizeof(GUID)]{};
        DWORD returned = 0;
        if (FAILED(CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_STANDARD, rootBuffer,
            static_cast<DWORD>(sizeof(rootBuffer)), &returned))) return S_FALSE;
        const auto* root = reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(rootBuffer);
        if (returned < FIELD_OFFSET(CF_SYNC_ROOT_STANDARD_INFO, SyncRootIdentity) + sizeof(GUID)
            || root->SyncRootIdentityLength != sizeof(GUID)
            || std::memcmp(root->SyncRootIdentity, &ProviderId, sizeof(GUID)) != 0) return S_FALSE;
        if (root->ProviderStatus == CF_PROVIDER_STATUS_ERROR || root->ProviderStatus == CF_PROVIDER_STATUS_TERMINATED)
            return S_FALSE;
        return S_OK;
    }
};

OBJECT_ENTRY_AUTO(__uuidof(SyncStatusOverlay), SyncStatusOverlay)

HRESULT WriteRegistryString(const wchar_t* keyPath, const wchar_t* name, const wchar_t* value)
{
    ATL::CRegKey key;
    auto status = key.Create(HKEY_LOCAL_MACHINE, keyPath, nullptr, REG_OPTION_NON_VOLATILE, KEY_WRITE | KEY_WOW64_64KEY);
    if (status == ERROR_SUCCESS) status = key.SetStringValue(name, value);
    return HRESULT_FROM_WIN32(status);
}

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved)
{
    return Module.DllMain(reason, reserved);
}

STDAPI DllCanUnloadNow() { return Module.DllCanUnloadNow(); }
STDAPI DllGetClassObject(REFCLSID classId, REFIID interfaceId, LPVOID* result)
{
    return Module.DllGetClassObject(classId, interfaceId, result);
}

STDAPI DllRegisterServer()
{
    wchar_t modulePath[32768]{};
    const DWORD length = GetModuleFileNameW(ATL::_AtlBaseModule.GetModuleInstance(), modulePath, ARRAYSIZE(modulePath));
    if (length == 0)
        return HRESULT_FROM_WIN32(GetLastError());
    if (length >= ARRAYSIZE(modulePath)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    ATL::CStringW classKey(L"SOFTWARE\\Classes\\CLSID\\");
    classKey += ClassId;
    const struct { ATL::CStringW Key; const wchar_t* Name; const wchar_t* Value; } entries[] = {
        {classKey, nullptr, OverlayDescription},
        {classKey + L"\\InprocServer32", nullptr, modulePath},
        {classKey + L"\\InprocServer32", L"ThreadingModel", L"Both"},
        {classKey + L"\\ProgID", nullptr, ProgId},
        {ATL::CStringW(L"SOFTWARE\\Classes\\") + ProgId, nullptr, OverlayDescription},
        {ATL::CStringW(L"SOFTWARE\\Classes\\") + ProgId + L"\\CLSID", nullptr, ClassId},
        {OverlayKey, nullptr, ClassId},
        {L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved", ClassId, OverlayDescription}
    };
    for (const auto& entry : entries)
    {
        const HRESULT result = WriteRegistryString(entry.Key, entry.Name, entry.Value);
        if (FAILED(result)) return result;
    }
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    ATL::CRegKey machine;
    auto status = machine.Open(HKEY_LOCAL_MACHINE, L"SOFTWARE", KEY_READ | KEY_WRITE | KEY_WOW64_64KEY);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    const ATL::CStringW classKey = ATL::CStringW(L"Classes\\CLSID\\") + ClassId;
    const ATL::CStringW progKey = ATL::CStringW(L"Classes\\") + ProgId;
    const wchar_t* keys[] = {classKey, progKey, OverlayKey};
    for (const auto* key : keys)
    {
        status = machine.RecurseDeleteKey(key);
        if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND) return HRESULT_FROM_WIN32(status);
    }
    ATL::CRegKey approved;
    status = approved.Open(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
        KEY_SET_VALUE | KEY_WOW64_64KEY);
    if (status == ERROR_SUCCESS)
    {
        status = approved.DeleteValue(ClassId);
        if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND) return HRESULT_FROM_WIN32(status);
    }
    else if (status != ERROR_FILE_NOT_FOUND) return HRESULT_FROM_WIN32(status);
    return S_OK;
}

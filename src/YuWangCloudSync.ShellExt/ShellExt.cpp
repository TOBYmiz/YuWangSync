#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <bcrypt.h>
#include <cfapi.h>
#include <atlbase.h>
#include <atlcom.h>
#include <atlstr.h>
#include <sddl.h>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

constexpr wchar_t ClassId[] = L"{B7519EF2-1F39-4612-B5AB-FD70C37E905B}";
constexpr wchar_t OverlayDescription[] = PRODUCT_NAME_EN_W L" synced overlay";
constexpr wchar_t ProgId[] = PRODUCT_IDENTIFIER_W L".SyncStatusOverlay.1";
constexpr wchar_t OverlayKey[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\" PRODUCT_IDENTIFIER_W;
constexpr GUID ProviderId = {0xA75C7E2E, 0x1048, 0x4EF8, {0xA1, 0x1D, 0x6A, 0x92, 0xB7, 0x84, 0x0B, 0x23}};

std::wstring SendProviderCommand(const std::wstring& request);

class ShellModule final : public ATL::CAtlDllModuleT<ShellModule> {};
ShellModule Module;

class ATL_NO_VTABLE __declspec(uuid("B7519EF2-1F39-4612-B5AB-FD70C37E905B")) SyncStatusOverlay :
    public ATL::CComObjectRootEx<ATL::CComMultiThreadModel>,
    public ATL::CComCoClass<SyncStatusOverlay, &__uuidof(SyncStatusOverlay)>,
    public IShellIconOverlayIdentifier,
    public IShellExtInit,
    public IContextMenu
{
    std::wstring _folderPath;
    bool _keptRefresh = false;

public:
    DECLARE_NO_REGISTRY()
    DECLARE_NOT_AGGREGATABLE(SyncStatusOverlay)
    BEGIN_COM_MAP(SyncStatusOverlay)
        COM_INTERFACE_ENTRY(IShellIconOverlayIdentifier)
        COM_INTERFACE_ENTRY(IShellExtInit)
        COM_INTERFACE_ENTRY(IContextMenu)
    END_COM_MAP()

    STDMETHOD(Initialize)(LPCITEMIDLIST folder, IDataObject* dataObject, HKEY) override
    {
        _folderPath.clear();
        _keptRefresh = false;
        // S_FALSE 仍属成功码，Explorer 会继续调用 QueryContextMenu；非本同步根时必须返回失败码，
        // 且只在全部校验通过后才写入 _folderPath。
        std::wstring candidate;
        if (dataObject != nullptr)
        {
            ATL::CComPtr<IShellItemArray> selectedItems;
            if (FAILED(SHCreateShellItemArrayFromDataObject(dataObject, IID_PPV_ARGS(&selectedItems))))
                return E_FAIL;
            DWORD count = 0;
            if (FAILED(selectedItems->GetCount(&count)) || count != 1) return E_FAIL;

            ATL::CComPtr<IShellItem> selectedItem;
            if (FAILED(selectedItems->GetItemAt(0, &selectedItem))) return E_FAIL;
            LPWSTR selectedPath = nullptr;
            if (FAILED(selectedItem->GetDisplayName(SIGDN_FILESYSPATH, &selectedPath)) || selectedPath == nullptr)
                return E_FAIL;
            candidate = selectedPath;
            CoTaskMemFree(selectedPath);
        }
        else
        {
            if (folder == nullptr) return E_FAIL;
            wchar_t path[32768]{};
            if (!SHGetPathFromIDListW(folder, path) || path[0] == L'\0') return E_FAIL;
            candidate = path;
        }

        if (!IsFolderInOwnSyncRoot(candidate)) return E_FAIL;

        _folderPath = std::move(candidate);
        const auto state = SendProviderCommand(L"refresh-state|" + _folderPath);
        _keptRefresh = state == L"kept";
        return S_OK;
    }

    static bool IsFolderInOwnSyncRoot(const std::wstring& path)
    {
        if (path.empty()) return false;
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) return false;

        alignas(CF_SYNC_ROOT_STANDARD_INFO) BYTE rootBuffer[sizeof(CF_SYNC_ROOT_STANDARD_INFO) + sizeof(GUID)]{};
        DWORD returned = 0;
        if (FAILED(CfGetSyncRootInfoByPath(path.c_str(), CF_SYNC_ROOT_INFO_STANDARD, rootBuffer,
            static_cast<DWORD>(sizeof(rootBuffer)), &returned))) return false;
        const auto* root = reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(rootBuffer);
        return returned >= FIELD_OFFSET(CF_SYNC_ROOT_STANDARD_INFO, SyncRootIdentity) + sizeof(GUID)
            && root->SyncRootIdentityLength == sizeof(GUID)
            && std::memcmp(root->SyncRootIdentity, &ProviderId, sizeof(GUID)) == 0;
    }

    STDMETHOD(QueryContextMenu)(HMENU menu, UINT index, UINT first, UINT last, UINT flags) override
    {
        if ((flags & CMF_DEFAULTONLY) != 0 || _folderPath.empty() || first + 1 > last) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        InsertMenuW(menu, index, MF_BYPOSITION | MF_STRING, first, L"刷新此目录");
        MENUITEMINFOW keepRefresh{};
        keepRefresh.cbSize = sizeof(keepRefresh);
        keepRefresh.fMask = MIIM_ID | MIIM_STRING | MIIM_STATE;
        keepRefresh.wID = first + 1;
        keepRefresh.dwTypeData = const_cast<LPWSTR>(L"保持目录刷新");
        keepRefresh.fState = _keptRefresh ? MFS_CHECKED : MFS_UNCHECKED;
        InsertMenuItemW(menu, index + 1, TRUE, &keepRefresh);
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 2);
    }

    STDMETHOD(InvokeCommand)(LPCMINVOKECOMMANDINFO command) override
    {
        if (command == nullptr || _folderPath.empty()) return E_INVALIDARG;
        if (HIWORD(command->lpVerb) != 0) return E_INVALIDARG;
        const auto verb = LOWORD(command->lpVerb);
        const auto request = verb == 0 ? L"refresh-folder|" + _folderPath
            : verb == 1 ? L"toggle-refresh-folder|" + _folderPath : std::wstring{};
        if (request.empty()) return E_INVALIDARG;
        return SendProviderCommand(request) == L"ok" ? S_OK : HRESULT_FROM_WIN32(ERROR_PIPE_NOT_CONNECTED);
    }

    STDMETHOD(GetCommandString)(UINT_PTR command, UINT flags, UINT*, LPSTR buffer, UINT capacity) override
    {
        if (buffer == nullptr || capacity == 0) return E_INVALIDARG;
        const char* value = nullptr;
        if (flags == GCS_VERBA) value = command == 0 ? "refreshfolder" : command == 1 ? "togglefolderrefresh" : nullptr;
        else if (flags == GCS_HELPTEXTA) value = command == 0 ? "Refresh this sync folder" : command == 1 ? "Set continuous refresh for this folder" : nullptr;
        if (value == nullptr) return E_INVALIDARG;
        strncpy_s(buffer, capacity, value, _TRUNCATE);
        return S_OK;
    }

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

std::wstring SendProviderCommand(const std::wstring& request)
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    DWORD needed = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
    std::vector<BYTE> tokenBuffer(needed);
    if (needed == 0 || !GetTokenInformation(token, TokenUser, tokenBuffer.data(), needed, &needed))
    {
        CloseHandle(token);
        return {};
    }
    CloseHandle(token);
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(tokenBuffer.data())->User.Sid, &sid)) return {};
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
    {
        LocalFree(sid);
        return {};
    }
    const std::wstring pipe = L"\\\\.\\pipe\\" PRODUCT_IDENTIFIER_W L".Stop." + std::wstring(sid)
        + L"." + std::to_wstring(session);
    LocalFree(sid);
    constexpr DWORD pipeTimeoutMs = 1200;
    const ULONGLONG deadline = GetTickCount64() + pipeTimeoutMs;
    if (!WaitNamedPipeW(pipe.c_str(), pipeTimeoutMs)) return {};
    ATL::CHandle handle(CreateFileW(pipe.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!handle) return {};

    const int length = WideCharToMultiByte(CP_UTF8, 0, request.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1) return {};
    std::vector<char> bytes(static_cast<size_t>(length));
    WideCharToMultiByte(CP_UTF8, 0, request.c_str(), -1, bytes.data(), length, nullptr, nullptr);
    bytes.back() = '\n';
    DWORD written = 0;
    if (!WriteFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr)
        || written != bytes.size()) return {};
    std::string response;
    while (response.size() < 32 && GetTickCount64() < deadline)
    {
        DWORD available = 0;
        if (!PeekNamedPipe(handle, nullptr, 0, nullptr, &available, nullptr)) return {};
        if (available == 0)
        {
            Sleep(10);
            continue;
        }
        char buffer[32]{};
        DWORD read = 0;
        const DWORD requested = available < sizeof(buffer) ? available : static_cast<DWORD>(sizeof(buffer));
        if (!ReadFile(handle, buffer, requested, &read, nullptr) || read == 0) return {};
        bool lineEnded = false;
        for (DWORD i = 0; i < read; ++i)
        {
            if (buffer[i] == '\n' || buffer[i] == '\r')
            {
                lineEnded = true;
                break;
            }
            response.push_back(buffer[i]);
        }
        if (lineEnded) break;
    }
    if (response.empty()) return {};
    const int wideLength = MultiByteToWideChar(CP_UTF8, 0, response.c_str(), -1, nullptr, 0);
    if (wideLength <= 1) return {};
    std::wstring result(static_cast<size_t>(wideLength), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, response.c_str(), -1, result.data(), wideLength);
    result.resize(static_cast<size_t>(wideLength - 1));
    return result;
}

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
        {ATL::CStringW(L"SOFTWARE\\Classes\\Directory\\Background\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W, nullptr, ClassId},
        {ATL::CStringW(L"SOFTWARE\\Classes\\Folder\\Background\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W, nullptr, ClassId},
        {ATL::CStringW(L"SOFTWARE\\Classes\\Directory\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W, nullptr, ClassId},
        {ATL::CStringW(L"SOFTWARE\\Classes\\Folder\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W, nullptr, ClassId},
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
    // machine is already opened at HKLM\SOFTWARE; all paths here are relative.
    const ATL::CStringW directoryBackgroundMenu = ATL::CStringW(L"Classes\\Directory\\Background\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W;
    const ATL::CStringW folderBackgroundMenu = ATL::CStringW(L"Classes\\Folder\\Background\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W;
    const ATL::CStringW directoryItemMenu = ATL::CStringW(L"Classes\\Directory\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W;
    const ATL::CStringW folderItemMenu = ATL::CStringW(L"Classes\\Folder\\shellex\\ContextMenuHandlers\\") + PRODUCT_IDENTIFIER_W;
    const wchar_t* keys[] = {classKey, progKey, OverlayKey + wcslen(L"SOFTWARE\\"),
        directoryBackgroundMenu, folderBackgroundMenu, directoryItemMenu, folderItemMenu};
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

"""统计目录内文件的真实磁盘占用（AllocationSize）+ 占位状态，用于脱水前后对比。

占位文件的 st_size 是"云端声明大小"（逻辑值），不代表磁盘占用；
必须查 AllocationSize 才能知道本地是否真的下载了数据。
"""
import os
import sys
import ctypes
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

CREATE_FILE = kernel32.CreateFileW
CREATE_FILE.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                        wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
CREATE_FILE.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.GetFileInformationByHandleEx.argtypes = [
    wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID, wintypes.DWORD]
kernel32.GetFileInformationByHandleEx.restype = wintypes.BOOL

FILE_READ_ATTRIBUTES = 0x0080
FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000
OPEN_EXISTING = 3
FILE_SHARE_ALL = 7
INVALID_HANDLE = ctypes.c_void_p(-1).value


class FILE_STANDARD_INFO(ctypes.Structure):
    _fields_ = [("AllocationSize", ctypes.c_longlong),
                ("EndOfFile", ctypes.c_longlong),
                ("NumberOfLinks", wintypes.DWORD),
                ("DeletePending", wintypes.BOOLEAN),
                ("Directory", wintypes.BOOLEAN)]


def alloc_size(path):
    h = CREATE_FILE(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, None,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, None)
    if h == INVALID_HANDLE or h is None:
        return None
    try:
        info = FILE_STANDARD_INFO()
        ok = kernel32.GetFileInformationByHandleEx(
            h, 1, ctypes.byref(info), ctypes.sizeof(info))
        return info.AllocationSize if ok else None
    finally:
        kernel32.CloseHandle(h)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else r"D:\YuWang"
    total = 0
    hydrated = 0
    files = 0
    details = []
    for dirpath, dirnames, filenames in os.walk(root):
        for fn in filenames:
            p = os.path.join(dirpath, fn)
            files += 1
            a = alloc_size(p)
            if a is None:
                continue
            if a > 0:
                hydrated += 1
                total += a
                details.append((a, p))
    details.sort(reverse=True)
    print(f"扫描文件总数={files}  已水化(占磁盘)={hydrated}  磁盘占用={total/1024/1024:.2f} MB")
    for a, p in details[:15]:
        print(f"  {a/1024/1024:9.2f} MB  {p}")


if __name__ == "__main__":
    main()

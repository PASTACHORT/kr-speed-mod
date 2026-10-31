"""Read-only probe: find live cocos2d objects in Kingdom Rush Vengeance by vtable."""
import ctypes, ctypes.wintypes as wt, struct, sys
from rtti import find_vtables

EXE = r"C:\Program Files (x86)\Steam\steamapps\common\Kingdom Rush Vengeance\Kingdom Rush Vengeance.exe"
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


k32.OpenProcess.restype = wt.HANDLE
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def open_proc(pid):
    return k32.OpenProcess(0x0400 | 0x0010, False, pid)  # QUERY_INFORMATION | VM_READ


def module_base(h):
    mods = (wt.HMODULE * 1024)()
    need = wt.DWORD()
    psapi.EnumProcessModulesEx(h, mods, ctypes.sizeof(mods), ctypes.byref(need), 3)
    return mods[0]


def read(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
        return None
    return buf.raw[:got.value]


def regions(h):
    addr = 0
    mbi = MBI()
    while k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        if mbi.State == 0x1000 and mbi.Type == 0x20000 and mbi.Protect in (0x04, 0x40):  # commit, private, RW/RWX
            yield mbi.BaseAddress, mbi.RegionSize
        addr = (mbi.BaseAddress or 0) + mbi.RegionSize
        if addr >= 0x7FFFFFFFFFFF:
            break


def scan(h, targets):
    """targets: {name: absolute vtable address}; returns {name: [instance addresses]}"""
    want = {struct.pack("<Q", v): n for n, v in targets.items()}
    found = {n: [] for n in targets}
    for base, size in regions(h):
        for off in range(0, size, 1 << 22):
            chunk = read(h, base + off, min(1 << 22, size - off))
            if not chunk:
                continue
            for pat, name in want.items():
                i = chunk.find(pat)
                while i >= 0:
                    if i % 8 == 0:
                        found[name].append(base + off + i)
                    i = chunk.find(pat, i + 1)
    return found


if __name__ == "__main__":
    # usage: python memscan.py <pid> ["<path to Kingdom Rush Vengeance.exe>"]
    pid = int(sys.argv[1])
    if len(sys.argv) > 2:
        EXE = sys.argv[2]
    names = ["Scheduler@cocos2d", "Director@cocos2d", "KRGameplayScene", "KRMapScene", "KRGamePause", "KRComic"]
    img, vts = find_vtables(EXE, names)
    h = open_proc(pid)
    base = module_base(h)
    print("module base %#x" % base)
    targets = {n: base + v[0] for n, v in vts.items() if v}
    found = scan(h, targets)
    for n, lst in found.items():
        print(n, [hex(a) for a in lst[:6]], "count", len(lst))
    for a in found["Scheduler@cocos2d"][:2]:
        raw = read(h, a, 0x40)
        print("Scheduler @%#x floats:" % a, [round(x, 3) for x in struct.unpack("<16f", raw)])

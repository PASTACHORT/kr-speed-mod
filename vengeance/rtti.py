"""Find MSVC x64 vtable RVAs for classes by RTTI name in a PE file (static, read-only)."""
import struct, sys


def load_pe(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt_size = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    image_base = struct.unpack_from("<Q", d, opt + 24)[0]
    secs = []
    for i in range(nsec):
        o = opt + opt_size + i * 40
        name = d[o:o + 8].rstrip(b"\0").decode()
        vsize, va, rsize, raw = struct.unpack_from("<IIII", d, o + 8)
        secs.append((name, va, vsize, raw, rsize))
    return d, image_base, secs


def off2rva(secs, off):
    for _, va, vs, raw, rs in secs:
        if raw <= off < raw + rs:
            return va + off - raw
    return None


def rva2off(secs, rva):
    for _, va, vs, raw, rs in secs:
        if va <= rva < va + rs:
            return raw + rva - va
    return None


def find_vtables(path, names):
    d, base, secs = load_pe(path)
    rdata = [s for s in secs if s[0] in (".rdata", ".data")]
    out = {}
    for name in names:
        tag = (".?AV" + name + "@@").encode() + b"\0"
        idx = d.find(tag)
        if idx < 0:
            out[name] = None
            continue
        td_rva = off2rva(secs, idx - 16)  # TypeDescriptor: pVFTable, spare, name
        res = []
        # Complete Object Locator: sig=1, offset, cdOffset, pTypeDescriptor, pClassDescriptor, pSelf
        needle = struct.pack("<I", td_rva)
        start = 0
        while True:
            j = d.find(needle, start)
            if j < 0:
                break
            start = j + 1
            col = j - 12
            if col < 0:
                continue
            sig, offs, cd, tdr, cdr, selfr = struct.unpack_from("<IIIIII", d, col)
            if sig == 1 and tdr == td_rva and selfr == off2rva(secs, col) and offs == 0:
                col_va = base + selfr
                k = d.find(struct.pack("<Q", col_va))
                while k >= 0:
                    res.append(off2rva(secs, k) + 8)  # vtable starts after the COL pointer
                    k = d.find(struct.pack("<Q", col_va), k + 1)
        out[name] = res
    return base, out


if __name__ == "__main__":
    base, vt = find_vtables(sys.argv[1], sys.argv[2:])
    print("image base %#x" % base)
    for k, v in vt.items():
        print(k, [hex(x) for x in v] if v else v)

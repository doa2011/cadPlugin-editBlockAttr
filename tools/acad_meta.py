# -*- coding: utf-8 -*-
"""
.NET 程序集元数据小工具：读取 / 定向 AssemblyRef 版本号。

用途
----
本机只有 AutoCAD 2010 (18.0.0.0) 与 2024 (24.3.0.0) 的托管 DLL，
无法为每个年份准备原版 SDK。构建时统一用其中一套「编译期引用副本」
编译，再把这些产物里 acmgd / acdbmgd / accoremgd 三条 AssemblyRef
的版本号改写为该年份 AutoCAD 真实的程序集版本，使之精确匹配、
不依赖任何 bindingRedirect 或 publisher policy。

本工具只改写我方未签名的托管 DLL 中的 8 字节版本字段，
不改动 IL、不改动 AutoCAD 自带 DLL，也不参与运行。
"""
import struct

AUTO_CAD_NAMES = (b'acmgd', b'acdbmgd', b'accoremgd')


def parse(path):
    d = bytearray(open(path, 'rb').read())
    pe = struct.unpack_from('<I', d, 0x3c)[0]
    machine = struct.unpack_from('<H', d, pe + 4)[0]
    magic = struct.unpack_from('<H', d, pe + 24)[0]
    dd = pe + 24 + (112 if magic == 0x20b else 96)
    cli_rva = struct.unpack_from('<I', d, dd + 14 * 8)[0]
    nsec = struct.unpack_from('<H', d, pe + 6)[0]
    optsz = struct.unpack_from('<H', d, pe + 20)[0]
    off = pe + 24 + optsz
    secs = []
    for i in range(nsec):
        b = off + i * 40
        vs, va, rs, rp = struct.unpack_from('<IIII', d, b + 8)
        secs.append((va, vs, rp, rs))

    def r2o(rva):
        for va, vs, rp, rs in secs:
            if va <= rva < va + max(vs, rs):
                return rp + (rva - va)
        return None

    cli = r2o(cli_rva)
    md = r2o(struct.unpack_from('<I', d, cli + 8)[0])
    vlen = struct.unpack_from('<I', d, md + 12)[0]
    runtime = bytes(d[md + 16:md + 16 + vlen]).rstrip(b'\0').decode('ascii', 'replace')

    p = md + 16 + vlen
    ns = struct.unpack_from('<H', d, p + 2)[0]
    q = p + 4
    streams = {}
    for i in range(ns):
        so, sz = struct.unpack_from('<II', d, q)
        q += 8
        e = d.index(b'\0', q)
        name = bytes(d[q:e]).decode('ascii', 'replace')
        q += (len(name) + 1 + 3) & ~3
        streams[name] = (md + so, sz)

    tn = '#~' if '#~' in streams else '#-'
    ts, tsz = streams[tn]
    heap = d[ts + 6]
    valid = struct.unpack_from('<Q', d, ts + 8)[0]
    ntab = bin(valid).count('1')
    rows = {}
    k = 0
    for tid in range(64):
        if valid >> tid & 1:
            rows[tid] = struct.unpack_from('<I', d, ts + 24 + 4 * k)[0]
            k += 1

    str_sz = 4 if heap & 1 else 2
    blob_sz = 4 if heap & 4 else 2
    sbase, ssize = streams['#Strings']

    def gs(idx):
        if idx >= ssize:
            return b''
        start = sbase + idx
        e = d.find(b'\0', start, sbase + ssize)
        return bytes(d[start:e if e >= 0 else sbase + ssize])

    return dict(data=d, runtime=runtime, machine='0x%04x' % machine,
                ts=ts, tsz=tsz, str_sz=str_sz, blob_sz=blob_sz,
                gs=gs, rows=rows, path=path)


def assembly_refs(m, names=None):
    """列出 AssemblyRef 表中名字匹配的记录及其版本字段偏移。"""
    names = names or AUTO_CAD_NAMES
    d = m['data']
    ts, tsz = m['ts'], m['tsz']
    str_sz, blob_sz = m['str_sz'], m['blob_sz']
    fmt = '<H' if str_sz == 2 else '<I'
    out = []
    k = ts + 8
    end = ts + tsz - (20 + 2 * str_sz + 2 * blob_sz)
    while k < end:
        maj, mnr, bld, rev = struct.unpack_from('<HHHH', d, k)
        if 1 <= maj <= 40 and 0 <= mnr <= 9 and bld == 0 and rev == 0:
            flags = struct.unpack_from('<I', d, k + 8)[0]
            if flags in (0, 1):
                ni = struct.unpack_from(fmt, d, k + 12 + blob_sz)[0]
                name = m['gs'](ni)
                if name.lower() in names:
                    out.append(dict(offset=k, name=name.decode('ascii', 'replace'),
                                    version=(maj, mnr, bld, rev)))
        k += 1
    return out


def retarget(src, dst, version, names=None):
    """把 AutoCAD 引用的版本号改成 version=(major,minor,build,rev) 并写出。"""
    m = parse(src)
    refs = assembly_refs(m, names)
    if not refs:
        raise RuntimeError('未找到 AutoCAD 程序集引用: %s' % src)
    d = m['data']
    for r in refs:
        struct.pack_into('<HHHH', d, r['offset'], *version)
    open(dst, 'wb').write(bytes(d))
    return refs


def inspect(path):
    m = parse(path)
    refs = assembly_refs(m)
    print('%-52s runtime=%-12s machine=%s' % (
        path.rsplit('\\', 1)[-1], m['runtime'], m['machine']))
    for r in refs:
        print('    %-14s %d.%d.%d.%d  @0x%x' % (
            r['name'], r['version'][0], r['version'][1],
            r['version'][2], r['version'][3], r['offset']))
    return m, refs


if __name__ == '__main__':
    import sys
    for p in sys.argv[1:]:
        inspect(p)

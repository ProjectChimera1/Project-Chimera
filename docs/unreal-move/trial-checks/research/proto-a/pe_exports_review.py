import struct, sys
d = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
optsz = struct.unpack_from('<H', d, pe + 20)[0]
opt = pe + 24
magic = struct.unpack_from('<H', d, opt)[0]
ddir = opt + (112 if magic == 0x20b else 96)
exp_rva, exp_sz = struct.unpack_from('<II', d, ddir)
secs = []
s0 = opt + optsz
for i in range(nsec):
    o = s0 + 40 * i
    vsz, va, rsz, rptr = struct.unpack_from('<IIII', d, o + 8)
    secs.append((va, max(vsz, rsz), rptr))
def r2o(rva):
    for va, sz, p in secs:
        if va <= rva < va + sz:
            return rva - va + p
    raise ValueError(rva)
e = r2o(exp_rva)
nnames, afn, anames = struct.unpack_from('<I', d, e + 24)[0], 0, struct.unpack_from('<I', d, e + 32)[0]
for i in range(nnames):
    nr = struct.unpack_from('<I', d, r2o(anames) + 4 * i)[0]
    o = r2o(nr)
    print(d[o:d.index(b'\0', o)].decode())

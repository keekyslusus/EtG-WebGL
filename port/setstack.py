import struct, sys
p, size = sys.argv[1], int(sys.argv[2])
d = bytearray(open(p, 'rb').read())
pe = struct.unpack_from('<I', d, 0x3C)[0]
assert d[pe:pe+4] == b'PE\0\0'
opt = pe + 24
magic = struct.unpack_from('<H', d, opt)[0]
assert magic == 0x20B, hex(magic)  # PE32+
old = struct.unpack_from('<Q', d, opt + 72)[0]
struct.pack_into('<Q', d, opt + 72, size)
open(p, 'wb').write(d)
print('SizeOfStackReserve %d -> %d' % (old, size))

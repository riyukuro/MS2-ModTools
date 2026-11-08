"""String.GetHashCode() of .NET Framework 2.0-3.5 on x86 (what Toolbench uses for flat model/property/behavior ids)."""
import struct

M = 0xFFFFFFFF


def _sar27(v):
    """C# int >> 27 (arithmetic) on a uint32 holding an int."""
    if v & 0x80000000:
        return ((v >> 27) | 0xFFFFFFE0) & M
    return v >> 27


def net_hash(s):
    data = s.encode('utf-16-le') + b'\0\0'
    while len(data) % 4:
        data += b'\0'
    ints = struct.unpack('<%dI' % (len(data) // 4), data)
    h1 = h2 = 352654597
    length = len(s)
    i = 0
    while length > 2:
        h1 = ((((h1 << 5) + h1) & M) + _sar27(h1) & M) ^ ints[i]
        h2 = ((((h2 << 5) + h2) & M) + _sar27(h2) & M) ^ ints[i + 1]
        i += 2
        length -= 4
    if length > 0:
        h1 = ((((h1 << 5) + h1) & M) + _sar27(h1) & M) ^ ints[i]
    return (h1 + h2 * 1566083941) & M

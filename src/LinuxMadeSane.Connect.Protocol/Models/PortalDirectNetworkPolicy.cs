// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
namespace LinuxMadeSane.Connect.Protocol.Models;
public static class PortalDirectNetworkPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
        var n=BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        (uint Network,int Bits)[] reserved=[(0x00000000,8),(0x0a000000,8),(0x64400000,10),(0x7f000000,8),(0xa9fe0000,16),(0xac100000,12),(0xc0000000,24),(0xc0000200,24),(0xc0586300,24),(0xc0a80000,16),(0xc6120000,15),(0xc6336400,24),(0xcb007100,24),(0xe0000000,3)];
        return !reserved.Any(x=>(n&(uint.MaxValue<<(32-x.Bits)))==x.Network);
    }
}

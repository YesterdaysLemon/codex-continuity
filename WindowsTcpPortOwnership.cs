using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexContinuity;

internal static class WindowsTcpPortOwnership
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpStateListen = 2;
    private const int TcpTableOwnerPidListener = 3;

    internal static bool IsLoopbackListenerOwnedBy(int port, int processId)
    {
        LoopbackEndpoint.ValidatePort(port);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: false,
            checked((int)AddressFamily.InterNetwork),
            TcpTableOwnerPidListener,
            reserved: 0);
        if (result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(checked((int)result), "Could not size the TCP owner table.");
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferLength,
                sort: false,
                checked((int)AddressFamily.InterNetwork),
                TcpTableOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception(
                    checked((int)result),
                    "Could not read the TCP owner table.");
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            var rowAddress = IntPtr.Add(buffer, sizeof(int));
            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(
                    IntPtr.Add(rowAddress, index * rowSize));
                if (row.State == TcpStateListen &&
                    row.OwningProcessId == processId &&
                    DecodePort(row.LocalPort) == port &&
                    new IPAddress(row.LocalAddress).Equals(IPAddress.Loopback))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int DecodePort(uint encodedPort)
    {
        var bytes = BitConverter.GetBytes(encodedPort);
        return (bytes[0] << 8) | bytes[1];
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TcpRowOwnerPid
    {
        internal readonly uint State;
        internal readonly uint LocalAddress;
        internal readonly uint LocalPort;
        internal readonly uint RemoteAddress;
        internal readonly uint RemotePort;
        internal readonly int OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int addressFamily,
        int tableClass,
        uint reserved);
}

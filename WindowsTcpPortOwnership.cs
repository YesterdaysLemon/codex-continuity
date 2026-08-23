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
    private const int MaximumTableBytes = 4 * 1024 * 1024;
    private const int MaximumReadAttempts = 4;

    internal delegate uint TcpTableReader(IntPtr table, ref int size);

    internal static bool IsLoopbackListenerOwnedBy(int port, int processId)
        => IsLoopbackListenerOwnedBy(port, processId, ReadNativeTable);

    internal static bool IsLoopbackListenerOwnedBy(
        int port,
        int processId,
        TcpTableReader readTable)
    {
        LoopbackEndpoint.ValidatePort(port);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        ArgumentNullException.ThrowIfNull(readTable);

        var bufferLength = 0;
        var result = readTable(IntPtr.Zero, ref bufferLength);
        if (result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(checked((int)result), "Could not size the TCP owner table.");
        }

        for (var attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            if (bufferLength < sizeof(int) || bufferLength > MaximumTableBytes)
            {
                throw new InvalidDataException(
                    $"TCP owner table size {bufferLength} is outside the allowed range.");
            }
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                result = readTable(buffer, ref bufferLength);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (result != 0)
                {
                    throw new Win32Exception(
                        checked((int)result),
                        "Could not read the TCP owner table.");
                }
                return ContainsOwnedListener(buffer, bufferLength, port, processId);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new IOException("The TCP owner table changed too often to verify safely.");
    }

    private static bool ContainsOwnedListener(
        IntPtr buffer,
        int bufferLength,
        int port,
        int processId)
    {
        var rowCount = Marshal.ReadInt32(buffer);
        var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
        if (rowCount < 0 || rowCount > (bufferLength - sizeof(int)) / rowSize)
        {
            throw new InvalidDataException("TCP owner table row count exceeds its buffer.");
        }
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

    private static uint ReadNativeTable(IntPtr table, ref int size) => GetExtendedTcpTable(
        table,
        ref size,
        sort: false,
        checked((int)AddressFamily.InterNetwork),
        TcpTableOwnerPidListener,
        reserved: 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int addressFamily,
        int tableClass,
        uint reserved);
}

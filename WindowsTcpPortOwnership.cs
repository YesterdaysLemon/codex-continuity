using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexContinuity;

internal static class WindowsTcpPortOwnership
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpStateListen = 2;
    private const uint TcpStateEstablished = 5;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerPidAll = 5;
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

        return ReadTable(
            readTable,
            (buffer, length) => ContainsOwnedListener(buffer, length, port, processId));
    }

    internal static bool IsLoopbackConnectionAcceptedBy(
        TcpClient connection,
        int processId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        if (connection.Client.LocalEndPoint is not IPEndPoint local ||
            connection.Client.RemoteEndPoint is not IPEndPoint remote)
        {
            return false;
        }
        local = NormalizeIpv4MappedEndpoint(local);
        remote = NormalizeIpv4MappedEndpoint(remote);
        if (!local.Address.Equals(IPAddress.Loopback) ||
            !remote.Address.Equals(IPAddress.Loopback))
        {
            return false;
        }

        return ReadTable(
            ReadNativeAllTable,
            (buffer, length) => ContainsOwnedConnection(
                buffer,
                length,
                local,
                remote,
                processId));
    }

    private static bool ReadTable(
        TcpTableReader readTable,
        Func<IntPtr, int, bool> containsOwnedEndpoint)
    {

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
                return containsOwnedEndpoint(buffer, bufferLength);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new IOException("The TCP owner table changed too often to verify safely.");
    }

    private static bool ContainsOwnedConnection(
        IntPtr buffer,
        int bufferLength,
        IPEndPoint relay,
        IPEndPoint backend,
        int processId) => ContainsRow(
            buffer,
            bufferLength,
            row => row.State == TcpStateEstablished &&
                row.OwningProcessId == processId &&
                DecodeEndpoint(row.LocalAddress, row.LocalPort).Equals(backend) &&
                DecodeEndpoint(row.RemoteAddress, row.RemotePort).Equals(relay));

    private static bool ContainsOwnedListener(
        IntPtr buffer,
        int bufferLength,
        int port,
        int processId)
    {
        return ContainsRow(
            buffer,
            bufferLength,
            row => row.State == TcpStateListen &&
                row.OwningProcessId == processId &&
                DecodePort(row.LocalPort) == port &&
                new IPAddress(row.LocalAddress).Equals(IPAddress.Loopback));
    }

    private static bool ContainsRow(
        IntPtr buffer,
        int bufferLength,
        Func<TcpRowOwnerPid, bool> predicate)
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
            if (predicate(row))
            {
                return true;
            }
        }
        return false;
    }

    private static IPEndPoint DecodeEndpoint(uint address, uint port) =>
        new(new IPAddress(address), DecodePort(port));

    private static IPEndPoint NormalizeIpv4MappedEndpoint(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6
            ? new(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

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

    private static uint ReadNativeAllTable(IntPtr table, ref int size) => GetExtendedTcpTable(
        table,
        ref size,
        sort: false,
        checked((int)AddressFamily.InterNetwork),
        TcpTableOwnerPidAll,
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

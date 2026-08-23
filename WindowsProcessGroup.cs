using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexContinuity;

internal sealed class WindowsProcessGroup : IDisposable
{
    private const uint CtrlBreakEvent = 1;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const nint ProcThreadAttributeHandleList = 0x00020002;
    private const short SwHide = 0;
    private const int ErrorInvalidHandle = 6;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    private readonly SafeProcessHandle nativeHandle;
    private readonly Process process;
    private int disposed;

    private WindowsProcessGroup(
        Process process,
        SafeProcessHandle nativeHandle,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        this.process = process;
        this.nativeHandle = nativeHandle;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    internal int Id => process.Id;
    internal bool HasExited => IsSignaled();
    internal int ExitCode
    {
        get
        {
            if (!HasExited)
            {
                throw new InvalidOperationException("The process group is still running.");
            }
            if (!GetExitCodeProcess(nativeHandle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not read exit code for process group {process.Id}.");
            }
            return unchecked((int)exitCode);
        }
    }
    internal DateTimeOffset StartedAtUtc => process.StartTime.ToUniversalTime();
    internal string ExecutablePath
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var path = new StringBuilder(capacity: 32 * 1024);
            var length = path.Capacity;
            if (!QueryFullProcessImageName(nativeHandle, flags: 0, path, ref length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not resolve executable path for process group {process.Id}.");
            }
            return path.ToString();
        }
    }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    internal static WindowsProcessGroup Attach(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        var process = Process.GetProcessById(processId);
        SafeProcessHandle? nativeHandle = null;
        try
        {
            nativeHandle = OpenProcess(
                ProcessQueryLimitedInformation | Synchronize,
                inheritHandle: false,
                checked((uint)processId));
            if (nativeHandle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not attach to process group {processId}.");
            }
            var attachment = new WindowsProcessGroup(
                process,
                nativeHandle,
                StreamReader.Null,
                StreamReader.Null);
            nativeHandle = null;
            process = null!;
            return attachment;
        }
        finally
        {
            nativeHandle?.Dispose();
            process?.Dispose();
        }
    }

    internal static WindowsProcessGroup Start(
        ProcessStartInfo startInfo,
        Action<int>? afterProcessCreated = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows process groups require the Windows console API.");
        }
        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException(
                "Windows process groups cannot use shell execution.",
                nameof(startInfo));
        }
        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new ArgumentException(
                "Use ArgumentList so each child argument can be quoted safely.",
                nameof(startInfo));
        }
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("A child executable is required.", nameof(startInfo));
        }
        if (!Path.IsPathFullyQualified(startInfo.FileName))
        {
            throw new ArgumentException(
                "The child executable path must be fully qualified.",
                nameof(startInfo));
        }

        EnsureConsole();
        using var standardInput = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        var standardOutput = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var standardError = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var processInformation = default(ProcessInformation);
        Process? process = null;
        var environmentBlock = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var inheritedHandlesPin = default(GCHandle);
        SafeProcessHandle? nativeHandle = null;

        try
        {
            var inheritedHandles = new[]
            {
                ClientHandle(standardInput),
                ClientHandle(standardOutput),
                ClientHandle(standardError),
            };
            inheritedHandlesPin = GCHandle.Alloc(inheritedHandles, GCHandleType.Pinned);
            attributeList = CreateHandleList(inheritedHandlesPin, inheritedHandles.Length);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseShowWindow | StartfUseStdHandles,
                    ShowWindow = SwHide,
                    StandardInput = inheritedHandles[0],
                    StandardOutput = inheritedHandles[1],
                    StandardError = inheritedHandles[2],
                },
                AttributeList = attributeList,
            };
            environmentBlock = CreateEnvironmentBlock(startInfo);
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var created = CreateProcess(
                Path.GetFullPath(startInfo.FileName),
                commandLine,
                processAttributes: IntPtr.Zero,
                threadAttributes: IntPtr.Zero,
                inheritHandles: true,
                creationFlags:
                    CreateNewProcessGroup |
                    CreateUnicodeEnvironment |
                    ExtendedStartupInfoPresent,
                environmentBlock,
                string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                    ? null
                    : startInfo.WorkingDirectory,
                ref startupInfo,
                out processInformation);
            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not start {startInfo.FileName} in a Windows process group.");
            }

            try
            {
                process = Process.GetProcessById(checked((int)processInformation.ProcessId));
                afterProcessCreated?.Invoke(process.Id);
            }
            catch
            {
                var terminated = TerminateProcess(processInformation.Process, exitCode: 1);
                if (process is not null)
                {
                    if (!terminated && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    WaitForTerminatedProcess(process);
                    process.Dispose();
                    process = null;
                }
                throw;
            }

            standardInput.DisposeLocalCopyOfClientHandle();
            standardOutput.DisposeLocalCopyOfClientHandle();
            standardError.DisposeLocalCopyOfClientHandle();
            nativeHandle = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            processInformation.Process = IntPtr.Zero;
            return new WindowsProcessGroup(
                process ?? throw new InvalidOperationException("The child process was not captured."),
                nativeHandle,
                CreateReader(standardOutput),
                CreateReader(standardError));
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    WaitForTerminatedProcess(process);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception)
                {
                }
                process.Dispose();
            }
            nativeHandle?.Dispose();
            standardOutput.Dispose();
            standardError.Dispose();
            throw;
        }
        finally
        {
            if (processInformation.Thread != IntPtr.Zero)
            {
                CloseHandle(processInformation.Thread);
            }
            if (processInformation.Process != IntPtr.Zero)
            {
                CloseHandle(processInformation.Process);
            }
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandlesPin.IsAllocated)
            {
                inheritedHandlesPin.Free();
            }
        }
    }

    internal bool SendCtrlBreak()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (HasExited)
        {
            return false;
        }
        if (GenerateConsoleCtrlEvent(CtrlBreakEvent, checked((uint)process.Id)))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (HasExited)
        {
            return false;
        }
        throw new Win32Exception(
            error,
            $"Could not send Ctrl+Break to process group {process.Id}.");
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await process.WaitForExitAsync(cancellationToken);
        while (!IsSignaled())
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    internal void Kill() => process.Kill(entireProcessTree: true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        StandardOutput.Dispose();
        StandardError.Dispose();
        process.Dispose();
        nativeHandle.Dispose();
    }

    private static void EnsureConsole()
    {
        var processIds = new uint[1];
        if (GetConsoleProcessList(processIds, checked((uint)processIds.Length)) != 0)
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInvalidHandle)
        {
            throw new Win32Exception(error, "Could not inspect the Continuity console.");
        }
        if (!AllocConsole())
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not allocate a hidden console for graceful backend control.");
        }

        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
        {
            ShowWindow(consoleWindow, SwHide);
        }
    }

    private static void WaitForTerminatedProcess(Process process)
    {
        try
        {
            process.WaitForExit(milliseconds: 5000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool IsSignaled()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        return WaitForSingleObject(nativeHandle, milliseconds: 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            _ => throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not wait for process group {process.Id}."),
        };
    }

    private static StreamReader CreateReader(AnonymousPipeServerStream stream) => new(
        stream,
        Encoding.UTF8,
        detectEncodingFromByteOrderMarks: true,
        bufferSize: 4096,
        leaveOpen: false);

    private static IntPtr ClientHandle(AnonymousPipeServerStream pipe) =>
        new(long.Parse(pipe.GetClientHandleAsString(), CultureInfo.InvariantCulture));

    private static IntPtr CreateHandleList(GCHandle inheritedHandlesPin, int handleCount)
    {
        nuint attributeListSize = 0;
        InitializeProcThreadAttributeList(
            attributeList: IntPtr.Zero,
            attributeCount: 1,
            flags: 0,
            ref attributeListSize);
        if (attributeListSize == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not size the child handle allowlist.");
        }
        var attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(
                    attributeList,
                    attributeCount: 1,
                    flags: 0,
                    ref attributeListSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not initialize the child handle allowlist.");
            }
            initialized = true;
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    flags: 0,
                    ProcThreadAttributeHandleList,
                    inheritedHandlesPin.AddrOfPinnedObject(),
                    checked((nuint)(handleCount * IntPtr.Size)),
                    previousValue: IntPtr.Zero,
                    returnSize: IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not restrict inherited child handles.");
            }
            return attributeList;
        }
        catch
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static IntPtr CreateEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var entries = startInfo.Environment
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}");
        var block = $"{string.Join('\0', entries)}\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var command = new StringBuilder(QuoteArgument(startInfo.FileName));
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ').Append(QuoteArgument(argument));
        }
        return command.ToString();
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environmentBlock,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processIds,
        uint processCount);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeProcessHandle process,
        out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2;
        internal IntPtr ReservedPointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }
}

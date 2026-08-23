using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexContinuity;

internal sealed class WindowsProcessGroup : IDisposable
{
    private const uint CtrlBreakEvent = 1;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const int ErrorInvalidHandle = 6;

    private readonly Process process;
    private int disposed;

    private WindowsProcessGroup(
        Process process,
        AnonymousPipeServerStream standardOutput,
        AnonymousPipeServerStream standardError)
    {
        this.process = process;
        StandardOutput = new StreamReader(
            standardOutput,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        StandardError = new StreamReader(
            standardError,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
    }

    internal int Id => process.Id;

    internal bool HasExited => process.HasExited;

    internal int ExitCode => process.ExitCode;

    internal StreamReader StandardOutput { get; }

    internal StreamReader StandardError { get; }

    internal static WindowsProcessGroup Start(ProcessStartInfo startInfo)
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
        var environmentBlock = IntPtr.Zero;

        try
        {
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseShowWindow | StartfUseStdHandles,
                ShowWindow = SwHide,
                StandardInput = ClientHandle(standardInput),
                StandardOutput = ClientHandle(standardOutput),
                StandardError = ClientHandle(standardError),
            };
            environmentBlock = CreateEnvironmentBlock(startInfo);
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var created = CreateProcess(
                applicationName: null,
                commandLine,
                processAttributes: IntPtr.Zero,
                threadAttributes: IntPtr.Zero,
                inheritHandles: true,
                creationFlags: CreateNewProcessGroup | CreateUnicodeEnvironment,
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

            Process process;
            try
            {
                process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            }
            catch
            {
                TerminateProcess(processInformation.Process, exitCode: 1);
                throw;
            }

            standardInput.DisposeLocalCopyOfClientHandle();
            standardOutput.DisposeLocalCopyOfClientHandle();
            standardError.DisposeLocalCopyOfClientHandle();
            return new WindowsProcessGroup(process, standardOutput, standardError);
        }
        catch
        {
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
        }
    }

    internal void SendCtrlBreak()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (process.HasExited)
        {
            throw new InvalidOperationException("The process group has already exited.");
        }
        if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, checked((uint)process.Id)))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not send Ctrl+Break to process group {process.Id}.");
        }
    }

    internal Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        process.WaitForExitAsync(cancellationToken);

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

    private static IntPtr ClientHandle(AnonymousPipeServerStream pipe) =>
        new(long.Parse(pipe.GetClientHandleAsString(), CultureInfo.InvariantCulture));

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
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processIds,
        uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

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
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }
}

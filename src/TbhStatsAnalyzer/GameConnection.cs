using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TbhStatsAnalyzer;

public sealed class GameConnection : IAsyncDisposable
{
    public int Pid { get; private set; }
    private Process? process;
    private string bridgeName = "";
    public bool HasExited => process is null || process.HasExited;
    public static Process[] FindGames() => Process.GetProcessesByName("TaskBarHero").OrderBy(p => p.Id).ToArray();

    public async Task ConnectAsync(int pid, CancellationToken cancellationToken)
    {
        process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "TaskBarHero", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранный процесс не является TaskBarHero.");
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        if (!modules.Any(m => m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("В процессе ещё не загружена GameAssembly.dll.");
        Pid = pid;
        string payload = Path.Combine(AppContext.BaseDirectory, "TbhObserver.payload.dll");
        if (!File.Exists(payload)) throw new FileNotFoundException("Рядом с приложением отсутствует TbhObserver.payload.dll.", payload);
        // A loaded DLL is locked until the game exits. Cache by content so rebuilding is safe.
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payload)))[..16];
        bridgeName = $"TbhObserver_{hash}";
        string directory = Path.Combine(AppContext.BaseDirectory, "observer", hash);
        Directory.CreateDirectory(directory);
        string dll = Path.Combine(directory, bridgeName + ".dll");
        if (!File.Exists(dll)) File.Copy(payload, dll);
        if (!modules.Any(m => m.ModuleName.Equals(bridgeName + ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            await Task.Run(() => Inject(process, dll), cancellationToken);
        }
        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var s = await ReadAsync(0, cancellationToken);
                if (!string.IsNullOrEmpty(s.Error)) throw new InvalidOperationException($"Сборщик: {s.Error}");
                if (s.Ready) return;
            }
            catch (TimeoutException) when (i < 29) { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("Игра не передала состояние. Возобновите игру и повторите подключение.");
    }

    public async Task<Snapshot> ReadAsync(ulong since, CancellationToken cancellationToken)
    {
        if (HasExited) throw new InvalidOperationException("Игра завершилась. Запустите её и подключитесь снова.");
        return await RequestAsync($"snapshot {since}", cancellationToken);
    }
    private async Task<Snapshot> RequestAsync(string request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        await using var pipe = new NamedPipeClientStream(".", $"tbh-build-observer-{Pid}-{bridgeName}", PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1500, deadline.Token);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(request + "\n"), deadline.Token);
            byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, deadline.Token);
            int length = BitConverter.ToInt32(header);
            if (length <= 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Неверный размер ответа сборщика.");
            byte[] body = new byte[length]; await pipe.ReadExactlyAsync(body, deadline.Token);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(body, Snapshot.JsonOptions)
                ?? throw new InvalidDataException("Пустой ответ сборщика.");
            if (snapshot.Protocol != 1 || snapshot.Pid != Pid) throw new InvalidDataException("Несовместимый протокол сборщика.");
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Сборщик не ответил за 4 секунды.");
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                try { await RequestAsync("pause", CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
            }
            process.Dispose(); process = null;
        }
    }

    private static void Inject(Process game, string path)
    {
        using var handle = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, game.Id);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть игру. Уровень прав игры и анализатора должен совпадать.");
        if (!IsWow64Process(handle, out bool wow64)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (wow64) throw new InvalidOperationException("Поддерживается только 64-битная версия игры.");
        byte[] bytes = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0");
        nint buffer = VirtualAllocEx(handle, 0, (nuint)bytes.Length, 0x3000, 0x04);
        if (buffer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool canFree = true;
        try
        {
            if (!WriteProcessMemory(handle, buffer, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            nint loader = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loader == 0 || !GetModuleHandleEx(0x00000004 | 0x00000002, loader, out nint loaderModule))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var modulePath = new StringBuilder(32768);
            if (GetModuleFileName(loaderModule, modulePath, modulePath.Capacity) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            string moduleName = Path.GetFileName(modulePath.ToString());
            var remoteModule = game.Modules.Cast<ProcessModule>().First(m => m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            nint remoteLoader = remoteModule.BaseAddress + (loader - loaderModule);
            using var thread = CreateRemoteThread(handle, 0, 0, remoteLoader, buffer, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint wait = WaitForSingleObject(thread, 10000);
            if (wait != 0)
            {
                // The remote thread may still be using its argument. Do not free it on a timeout.
                canFree = false;
                throw new TimeoutException("Загрузка сборщика не завершилась. Перезапустите игру перед повторной попыткой.");
            }
            // The thread exit code truncates a 64-bit HMODULE; verify the loaded module instead.
            game.Refresh();
            if (!game.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Windows не загрузила TbhObserver.dll. Проверьте разрядность и доступ к файлу.");
        }
        finally { if (canFree) VirtualFreeEx(handle, buffer, 0, 0x8000); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileName(nint module, StringBuilder path, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stackSize, nint start, nint argument, uint flags, out uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}

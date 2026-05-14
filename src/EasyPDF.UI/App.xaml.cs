using EasyPDF.Application.ViewModels;
using EasyPDF.UI.Services;
using EasyPDF.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuPDFCore;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;

using WpfApplication = System.Windows.Application;

namespace EasyPDF.UI;

public partial class App : WpfApplication
{
    private const string MutexName = @"Global\EasyPDF_4F8E2A1D-3B7C-6509-A2D1-8E0F4C3B5A7E";
    internal const string PipeName = "EasyPDF_IPC_4F8E2A1D";

    private Mutex? _instanceMutex;
    private bool _mutexOwned;
    private IServiceProvider? _services;
    private ILogger<App>? _appLogger;
    private CancellationTokenSource? _pipeCts;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

       
        _instanceMutex = new Mutex(true, MutexName, out _mutexOwned);
        if (!_mutexOwned)
        {
            if (e.Args.Length > 0)
                TrySendFileToPipe(e.Args[0]);
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        _services = ServiceConfiguration.Build();
        _appLogger = _services.GetRequiredService<ILogger<App>>();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            _appLogger.LogCritical(ex, "Unhandled non-UI exception (terminating={T})", args.IsTerminating);
        };

      
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _appLogger.LogWarning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

    
        await SetupMuPdfRedirectAsync(_appLogger);

        // Initialize the view-model BEFORE the window is shown. The first paint then sees
        // the saved theme + recent files already loaded, and a file-arg drop below can't
        // race against an in-flight InitializeAsync inside OnSourceInitialized.
        var vm = _services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync();

        var mainWindow = new MainWindow(vm);
        mainWindow.Show();
        MainWindow = mainWindow;

        if (e.Args.Length > 0 && e.Args[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _ = vm.DropFileAsync(e.Args[0]);
        }

        
        WindowsFileAssociationService.EnsureRegistered();

        _pipeCts = new CancellationTokenSource();
        StartPipeServer(_services.GetRequiredService<MainViewModel>(), _pipeCts.Token);
    }

    private static async Task SetupMuPdfRedirectAsync(ILogger logger)
    {
        try
        {
            await MuPDF.RedirectOutput().ConfigureAwait(false);

            MuPDF.StandardErrorMessage += (_, args) =>
            {
                try { logger.LogDebug("[MuPDF] {Msg}", args.Message?.TrimEnd()); }
                catch { /* logger must never kill the background reader thread */ }
            };
            MuPDF.StandardOutputMessage += (_, args) =>
            {
                try { logger.LogTrace("[MuPDF] {Msg}", args.Message?.TrimEnd()); }
                catch { }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MuPDF output redirect failed; native warnings will appear in the console");
        }
    }


    // Hard cap on incoming pipe payload — far above any realistic Windows path length, but low
    // enough that a malicious local sender can't balloon memory or stall the reader.
    private const int MaxPipePayloadBytes = 4096;

    private void StartPipeServer(MainViewModel vm, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    string? raw = await ReadIncomingPathAsync(server, ct).ConfigureAwait(false);
                    if (IsAcceptableIncomingPath(raw, out string sanitized))
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() => { _ = vm.DropFileAsync(sanitized); }));
                    }
                    else if (raw is not null)
                    {
                        _appLogger?.LogWarning("Rejected pipe payload (length={Len})", raw.Length);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore pipe errors — just wait for next connection */ }
            }
        }, ct);
    }

    private static async Task<string?> ReadIncomingPathAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var buffer = new byte[MaxPipePayloadBytes];
        int total = 0;
        while (total < MaxPipePayloadBytes)
        {
            int n = await server.ReadAsync(buffer.AsMemory(total, MaxPipePayloadBytes - total), ct).ConfigureAwait(false);
            if (n == 0) break;

            int nl = Array.IndexOf(buffer, (byte)'\n', total, n);
            if (nl >= 0) { total = nl; break; }

            total += n;
        }
        return total == 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }

    private static bool IsAcceptableIncomingPath(string? raw, out string sanitized)
    {
        sanitized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Length > MaxPipePayloadBytes) return false;

        // Device-namespace prefixes (\\?\, \\.\) bypass Win32 path normalization and can address
        // raw devices — never legitimate as a "PDF the user wants opened".
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            raw.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;

        if (!raw.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;

        // Must be absolute: we can't safely resolve a relative path here — we don't know the
        // sender's working directory and accidentally falling back to ours could open the
        // wrong file.
        if (!Path.IsPathFullyQualified(raw)) return false;

        try
        {
            if (!File.Exists(raw)) return false;
        }
        catch { return false; }

        sanitized = raw;
        return true;
    }

    private static void TrySendFileToPipe(string filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(filePath);
        }
        catch { /* existing instance may not have the pipe up yet — that's fine */ }
    }


    protected override async void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _pipeCts = null;

        try
        {
            if (_mutexOwned)
            {
                _instanceMutex?.ReleaseMutex();
                _mutexOwned = false;
            }
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
        catch { }

        try
        {
            if (_services is IAsyncDisposable ad)
                await ad.DisposeAsync();
            else if (_services is IDisposable d)
                d.Dispose();
        }
        catch (Exception ex)
        {
            _appLogger?.LogWarning(ex, "Error during service disposal on exit");
        }

        base.OnExit(e);
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (existing is not null)
            {
                NativeMethods.ShowWindow(existing.MainWindowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(existing.MainWindowHandle);
            }
        }
        catch { /* non-critical — second instance simply exits */ }
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark handled so WPF doesn't tear the process down before we flush state.
        e.Handled = true;
        // Belt-and-braces: never let this handler itself throw — it would recursively re-fire.
        try { HandleCrashAndExit(e.Exception); }
        catch { /* last-resort swallow */ }
    }

    private void HandleCrashAndExit(Exception ex)
    {
        _appLogger?.LogCritical(ex, "Unhandled dispatcher exception — application will close");

        // Best-effort: flush per-tab last-page state before exit. Bounded wait so a hung
        // file lock can't trap the user in a half-dead app.
        try
        {
            if (_services?.GetService<MainViewModel>() is { } vm)
                Task.Run(() => vm.SaveLastPageAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception flushEx)
        {
            _appLogger?.LogWarning(flushEx, "Failed to flush state during crash recovery");
        }

        var choice = MessageBox.Show(
            $"EasyPDF encountered a critical error and must close.\n\n{ex.Message}\n\nWould you like to restart?",
            "EasyPDF — Critical Error",
            MessageBoxButton.YesNo, MessageBoxImage.Error);

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
            catch (Exception restartEx)
            {
                _appLogger?.LogWarning(restartEx, "Failed to restart application");
            }
        }

        Shutdown(1);
    }
}

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

// Disambiguate: EasyPDF.Application is our project namespace, System.Windows.Application is the WPF class
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
        // Wire up BEFORE any window opens so no exception slips through.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: if another EasyPDF process is already running,
        // bring it to the foreground and exit this one.
        _instanceMutex = new Mutex(true, MutexName, out _mutexOwned);
        if (!_mutexOwned)
        {
            // Forward the file path to the running instance before exiting.
            if (e.Args.Length > 0)
                TrySendFileToPipe(e.Args[0]);
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        _services = ServiceConfiguration.Build();
        _appLogger = _services.GetRequiredService<ILogger<App>>();

        // Catch exceptions thrown on non-UI background threads (ThreadPool, etc.).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            _appLogger.LogCritical(ex, "Unhandled non-UI exception (terminating={T})", args.IsTerminating);
        };

        // Catch fire-and-forget Tasks whose exceptions were never observed.
        // SetObserved() prevents the finalizer from re-throwing and suppresses
        // any further propagation (already a no-op in .NET 6+ but explicit is clearer).
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _appLogger.LogWarning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Redirect MuPDF native stderr/stdout to ILogger so warnings from
        // structurally non-standard PDFs don't appear in the terminal.
        // Awaited before Show() so the redirect is active for every native call
        // the app makes, including any triggered by the "Open with…" argument.
        await SetupMuPdfRedirectAsync(_appLogger);

        var mainWindow = new MainWindow(_services.GetRequiredService<MainViewModel>());
        mainWindow.Show();
        MainWindow = mainWindow;

        // Handle "Open with…" command-line argument
        if (e.Args.Length > 0 && e.Args[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var vm = _services.GetRequiredService<MainViewModel>();
            _ = vm.DropFileAsync(e.Args[0]);
        }

        // Register in Windows shell ("Open with EasyPDF") — skips if already registered
        // for the current exe path so subsequent launches incur only one registry read.
        WindowsFileAssociationService.EnsureRegistered();

        // Start IPC pipe so subsequent instances can forward their file paths here.
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
            // Redirect failure is non-fatal — MuPDF warnings simply go to stderr.
            logger.LogWarning(ex, "MuPDF output redirect failed; native warnings will appear in the console");
        }
    }

    // ─── Named pipe IPC ────────────────────────────────────────────────────────

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

                    using var reader = new StreamReader(server);
                    string? path = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        string filePath = path!;
                        _ = Dispatcher.BeginInvoke(new Action(() => { _ = vm.DropFileAsync(filePath); }));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore pipe errors — just wait for next connection */ }
            }
        }, ct);
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

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _pipeCts = null;

        // Release the single-instance mutex so the next launch can proceed.
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
            // Don't let a disposal error produce a dialog on app exit.
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
        _appLogger?.LogError(e.Exception, "Unhandled dispatcher exception");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will continue.",
            "EasyPDF – Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

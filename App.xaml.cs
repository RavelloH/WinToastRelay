using Microsoft.UI.Xaml;
using WinToastRelay.Services;
using H.NotifyIcon;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinToastRelay;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string MainInstanceKey = "WinToastRelay.Main";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static AppInstance? _mainInstance;

    public static NotificationRelayService RelayService { get; } = new();
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static async Task ShutdownAsync()
    {
        try
        {
            // Do not let a stalled network request or local file operation make the
            // tray Exit command appear unresponsive. The queue is persisted after
            // every enqueue/attempt, so a forced process exit is safe here.
            await RelayService.StopAsync().WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException)
        {
            // Continue to process termination after the bounded graceful-shutdown window.
        }
        finally
        {
            // Do not leave the process alive if a third-party endpoint or another
            // shutdown callback fails while the tray exit command is being handled.
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
            return;
        }

        _mainInstance = instance;
        _mainInstance.Activated += MainInstance_Activated;

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();

        if (activationArgs.Kind == ExtendedActivationKind.StartupTask)
        {
            DispatcherQueue.TryEnqueue(() => WindowExtensions.Hide(Window));
        }
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        // StartupTask activation is intentionally kept hidden in the tray. A normal
        // second launch should instead bring the existing window to the foreground.
        if (args.Kind == ExtendedActivationKind.StartupTask) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (Window is MainWindow mainWindow)
                mainWindow.ShowFromExternalActivation();
        });
    }
}

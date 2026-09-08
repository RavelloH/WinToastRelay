using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using H.NotifyIcon;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinToastRelay.Services;
using WinToastRelay.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinToastRelay;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1200;
    private const int DefaultWindowHeight = 800;
    private const int MinimumWindowWidth = 860;
    private const int MinimumWindowHeight = 620;
    private const int SwRestore = 9;

    private readonly TaskbarIcon _trayIcon;
    private readonly MenuFlyoutItem _relayToggleItem;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        // Keep the two-column navigation layout usable when the window is resized.
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));

        var trayMenu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "打开 WinToastRelay" };
        openItem.Click += OpenFromTray_Click;
        _relayToggleItem = new MenuFlyoutItem { Text = "开始转发" };
        _relayToggleItem.Click += RelayToggleFromTray_Click;
        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += ExitFromTray_Click;
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(_relayToggleItem);
        trayMenu.Items.Add(new MenuFlyoutSeparator());
        trayMenu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WinToastRelay",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            ContextFlyout = trayMenu,
            LeftClickCommand = new RelayCommand(ShowFromTray),
            DoubleClickCommand = new RelayCommand(ShowFromTray)
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        if (RootFrame.Content is MainPage page)
        {
            page.ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateRelayMenu(page.ViewModel.IsRelayRunning);
        }
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        _trayIcon.ForceCreate();
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isExiting) return;
        args.Cancel = true;
        WindowExtensions.Hide(this);
        _ = Task.Run(ProcessMemoryTrimmer.Trim);
    }

    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        var size = sender.Size;
        if (size.Width < MinimumWindowWidth || size.Height < MinimumWindowHeight)
        {
            sender.ResizeClient(new Windows.Graphics.SizeInt32(
                Math.Max(size.Width, MinimumWindowWidth),
                Math.Max(size.Height, MinimumWindowHeight)));
        }
    }

    private void OpenFromTray_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
    }

    private async void RelayToggleFromTray_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage page)
            await page.ViewModel.ToggleRelayCommand.ExecuteAsync(null);
        if (RootFrame.Content is MainPage updated)
            UpdateRelayMenu(updated.ViewModel.IsRelayRunning);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainPageViewModel.IsRelayRunning) && sender is MainPageViewModel vm)
            UpdateRelayMenu(vm.IsRelayRunning);
    }

    private void UpdateRelayMenu(bool isRunning)
    {
        _relayToggleItem.Text = isRunning ? "暂停转发" : "开始转发";
    }

    private void ShowFromTray()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        WindowExtensions.Show(this);
        Activate();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(windowHandle, SwRestore);
        SetForegroundWindow(windowHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private async void ExitFromTray_Click(object sender, RoutedEventArgs e)
    {
        if (_isExiting) return;
        _isExiting = true;
        try { _trayIcon.Dispose(); }
        catch { }
        await App.ShutdownAsync();
    }

    public void ShowFromExternalActivation() => ShowFromTray();
}

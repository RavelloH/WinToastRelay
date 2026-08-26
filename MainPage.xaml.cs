using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using WinToastRelay.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinToastRelay;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _initializing;
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        InitializeComponent();
        ViewModel = new MainPageViewModel(App.RelayService);
    }

    private async void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        await ViewModel.InitializeAsync();
        BearerTokenBox.Password = ViewModel.BearerToken;
        WxPusherAppTokenBox.Password = ViewModel.WxPusherAppToken;
        _initializing = true;
        LanguageCombo.SelectedIndex = ViewModel.IsChinese ? 0 : 1;
        _initializing = false;
        PlayContentTransition();
    }

    private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ViewModel.CurrentSection = "settings";
            PlayContentTransition();
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string section)
        {
            ViewModel.CurrentSection = section;
            PlayContentTransition();
        }
    }

    private void BearerTokenBox_PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            ViewModel.BearerToken = box.Password;
    }

    private void WxPusherAppTokenBox_PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            ViewModel.WxPusherAppToken = box.Password;
    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        var english = string.Equals(item.Tag?.ToString(), "en-US", StringComparison.OrdinalIgnoreCase);
        if (ViewModel.IsChinese == !english) return;
        if (ViewModel.IsChinese == english) await ViewModel.ToggleLanguageCommand.ExecuteAsync(null);
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.StartWithWindows) return;
        await ViewModel.ToggleStartupCommand.ExecuteAsync(null);
    }

    private async void RelayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.IsRelayRunning) return;
        await ViewModel.ToggleRelayCommand.ExecuteAsync(null);
    }

    private void ConfigureDestination_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[1];
    }

    private void BarkParametersTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox) ResizeBarkParametersTextBox(textBox);
    }

    private void BarkParametersTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox) ResizeBarkParametersTextBox(textBox);
    }

    private static void ResizeBarkParametersTextBox(TextBox textBox)
    {
        // Keep the editor compact for short parameter lists and grow it for multiline input.
        var lines = Math.Clamp(textBox.Text.Count(character => character == '\n') + 1, 3, 12);
        textBox.Height = Math.Clamp(48 + (lines * 24), 72, 336);
    }

    private void PlayContentTransition()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(340));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = easing };
        var translate = new DoubleAnimation { From = 28, To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, ContentRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(translate, ContentTranslateTransform);
        Storyboard.SetTargetProperty(translate, "Y");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(translate);
        storyboard.Begin();
    }
}

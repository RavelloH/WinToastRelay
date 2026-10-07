using CommunityToolkit.Mvvm.ComponentModel;

namespace WinToastRelay.ViewModels;

public sealed class WebhookSecretOption : ObservableObject
{
    private string _name = string.Empty;
    private string _value = string.Empty;
    private string _namePlaceholder = string.Empty;
    private string _valuePlaceholder = string.Empty;
    private string _removeLabel = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // This value is only kept in memory and is edited through a PasswordBox.
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string NamePlaceholder
    {
        get => _namePlaceholder;
        set => SetProperty(ref _namePlaceholder, value);
    }

    public string ValuePlaceholder
    {
        get => _valuePlaceholder;
        set => SetProperty(ref _valuePlaceholder, value);
    }

    public string RemoveLabel
    {
        get => _removeLabel;
        set => SetProperty(ref _removeLabel, value);
    }
}

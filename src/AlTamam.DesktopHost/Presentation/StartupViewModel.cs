using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AlTamam.DesktopHost.Presentation;

/// <summary>
/// Native WPF startup presentation state only. Database initialization, WebView2
/// navigation and authorization remain in the host/application layers.
/// </summary>
public sealed class StartupViewModel : INotifyPropertyChanged
{
    private string _message = "جارٍ تجهيز التطبيق...";
    private Visibility _panelVisibility = Visibility.Visible;

    public string Message
    {
        get => _message;
        set
        {
            if (string.Equals(_message, value, StringComparison.Ordinal)) return;
            _message = value;
            OnPropertyChanged();
        }
    }

    public Visibility PanelVisibility
    {
        get => _panelVisibility;
        set
        {
            if (_panelVisibility == value) return;
            _panelVisibility = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;
using Button = System.Windows.Controls.Button;

namespace BTAudioTray;

public partial class FlyoutWindow : Window
{
    private readonly ObservableCollection<DeviceItem> _items = new();

    /// <summary>Raised when the user clicks a device row.</summary>
    public event Action<DeviceItem>? DeviceActivated;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    public FlyoutWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _items;
    }

    /// <summary>Replace the device rows shown in the flyout.</summary>
    public void SetDevices(IEnumerable<DeviceItem> devices)
    {
        _items.Clear();
        foreach (var d in devices) _items.Add(d);
        bool empty = _items.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Find the row for an endpoint so callers can update its busy/connected state.</summary>
    public DeviceItem? Find(string endpointId)
    {
        foreach (var d in _items) if (d.EndpointId == endpointId) return d;
        return null;
    }

    /// <summary>Show the flyout anchored at the bottom-right (near the tray), fading in.</summary>
    public void ShowNearTray()
    {
        // Show first (off-screen / transparent) so layout runs and ActualHeight is real,
        // then snap to the corner. Positioning before the first layout pass put the window
        // off-screen because ActualHeight was still 0.
        Opacity = 0;
        Left = -10000;   // park off-screen for the initial layout pass
        Top = -10000;
        Show();
        Activate();

        Dispatcher.BeginInvoke(new Action(PositionBottomRight),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PositionBottomRight()
    {
        var work = SystemParameters.WorkArea; // WPF DIPs, already DPI-aware

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 200;

        Left = work.Right - w;
        Top = work.Bottom - h;

        // Clamp so we never land off the visible work area.
        if (Left < work.Left) Left = work.Left;
        if (Top < work.Top) Top = work.Top;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DeviceItem item)
            DeviceActivated?.Invoke(item);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // Never actually destroy the window on close; the tray app controls lifetime.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

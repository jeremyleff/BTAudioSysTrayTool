using System.ComponentModel;

namespace BTAudioTray;

/// <summary>
/// View model for one row in the flyout. Mirrors a <see cref="BluetoothManager.BtDevice"/>
/// plus presentation state (status text, whether it's the current default output).
/// </summary>
public sealed class DeviceItem : INotifyPropertyChanged
{
    public string EndpointId { get; }
    public string Name { get; }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { if (_isConnected != value) { _isConnected = value; Raise(nameof(IsConnected)); Raise(nameof(StatusText)); } }
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { if (_isDefault != value) { _isDefault = value; Raise(nameof(IsDefault)); Raise(nameof(StatusText)); } }
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set { if (_busy != value) { _busy = value; Raise(nameof(Busy)); Raise(nameof(StatusText)); } }
    }

    public string StatusText =>
        Busy ? "Working…"
        : IsConnected ? (IsDefault ? "Connected · default output" : "Connected")
        : "Not connected";

    internal DeviceItem(BluetoothManager.BtDevice dev, bool isDefault)
    {
        EndpointId = dev.EndpointId;
        Name = dev.Name;
        _isConnected = dev.IsConnected;
        _isDefault = isDefault;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

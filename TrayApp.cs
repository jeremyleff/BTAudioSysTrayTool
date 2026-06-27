using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace BTAudioTray;

/// <summary>
/// Orchestrates the tray presence (a WinForms NotifyIcon) and the WPF flyout. The flyout
/// is the modern UI; NotifyIcon only provides the icon in the notification area, since WPF
/// has no native tray support.
///
/// Device scanning runs in the background and feeds both the flyout rows and the
/// state-aware tray icon. Connect/disconnect go through BluetoothManager (audio-driver KS
/// reconnect) and AudioManager (default-output switch), with verified status.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly FlyoutWindow _flyout;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly Icon _iconConnected;
    private readonly Icon _iconDisconnected;
    private bool _scanning;

    public TrayApp()
    {
        _iconConnected = LoadEmbeddedIcon("connected.ico");
        _iconDisconnected = LoadEmbeddedIcon("disconnected.ico");

        _flyout = new FlyoutWindow();
        _flyout.DeviceActivated += OnDeviceActivated;
        _flyout.RefreshRequested += () => _ = ScanAsync(showInFlyout: true);
        _flyout.ExitRequested += ExitApp;

        _icon = new NotifyIcon
        {
            Icon = _iconDisconnected,
            Text = "BT Audio Tray",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                ToggleFlyout();
        };

        _scanTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _scanTimer.Tick += (_, _) => _ = ScanAsync(showInFlyout: false);
        _scanTimer.Start();

        _ = ScanAsync(showInFlyout: false);
    }

    private void ToggleFlyout()
    {
        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }
        // Refresh contents, then show.
        _ = ScanAsync(showInFlyout: true);
        _flyout.ShowNearTray();
    }

    /// <summary>Scan devices off the UI thread; update tray icon and (optionally) flyout.</summary>
    private async Task ScanAsync(bool showInFlyout)
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var devices = await Task.Run(BluetoothManager.GetBluetoothAudioDevices);
            string? defaultId = SafeDefaultId();

            var items = devices
                .Select(d => new DeviceItem(d, isDefault: d.EndpointId == defaultId))
                .ToList();

            _icon.Icon = devices.Any(d => d.IsConnected) ? _iconConnected : _iconDisconnected;
            _icon.Text = devices.Any(d => d.IsConnected) ? "BT Audio — connected" : "BT Audio Tray";

            if (showInFlyout || _flyout.IsVisible)
                _flyout.SetDevices(items);
        }
        catch
        {
            // Keep prior state on transient failures.
        }
        finally
        {
            _scanning = false;
        }
    }

    private async void OnDeviceActivated(DeviceItem item)
    {
        if (item.Busy) return;

        if (item.IsConnected)
        {
            item.Busy = true;
            try
            {
                await Task.Run(() => BluetoothManager.Disconnect(item.EndpointId));
                bool active = await WaitForActiveAsync(item.EndpointId, attempts: 16, wantActive: false);
                item.IsConnected = active;
                if (!active) ShowBalloon("Disconnected", $"{item.Name} disconnected.", ToolTipIcon.Info);
            }
            finally { item.Busy = false; _ = ScanAsync(showInFlyout: true); }
            return;
        }

        item.Busy = true;
        try
        {
            bool attempted = await Task.Run(() => BluetoothManager.Connect(item.EndpointId));
            if (!attempted)
            {
                ShowBalloon("Connect failed",
                    $"Couldn't reach {item.Name}. Take them out of the case and try again.",
                    ToolTipIcon.Warning);
                return;
            }

            bool active = await WaitForActiveAsync(item.EndpointId, attempts: 24, wantActive: true);
            item.IsConnected = active;
            if (!active)
            {
                ShowBalloon("Connecting…",
                    $"Asked {item.Name} to connect, but audio hasn't come up yet. " +
                    "If they're in the case, take them out and click again.", ToolTipIcon.Warning);
                return;
            }

            AudioManager.SetDefaultRenderEndpoint(item.EndpointId);

            string? nowDefault = null;
            for (int i = 0; i < 8; i++)
            {
                nowDefault = SafeDefaultId();
                if (nowDefault == item.EndpointId) break;
                await Task.Delay(150);
            }
            item.IsDefault = nowDefault == item.EndpointId;

            if (item.IsDefault)
                ShowBalloon("Ready", $"{item.Name} is connected and is now your sound output.", ToolTipIcon.Info);
            else
                ShowBalloon("Almost — switch didn't stick",
                    $"{item.Name} is connected, but Windows didn't accept it as default. Click again.",
                    ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowBalloon("Error", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            item.Busy = false;
            _ = ScanAsync(showInFlyout: true);
        }
    }

    private static async Task<bool> WaitForActiveAsync(string endpointId, int attempts, bool wantActive)
    {
        for (int i = 0; i < attempts; i++)
        {
            bool isActive = await Task.Run(() => BluetoothManager.IsEndpointActive(endpointId));
            if (isActive == wantActive) return isActive;
            await Task.Delay(300);
        }
        return await Task.Run(() => BluetoothManager.IsEndpointActive(endpointId));
    }

    private static string? SafeDefaultId()
    {
        try { return AudioManager.GetDefaultRenderEndpointId(); }
        catch { return null; }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(4000);
    }

    private static Icon LoadEmbeddedIcon(string fileName)
    {
        var asm = typeof(TrayApp).Assembly;
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream != null) return new Icon(stream);
        }
        return SystemIcons.Application;
    }

    private void ExitApp()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _scanTimer.Stop();
        _scanTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _iconConnected.Dispose();
        _iconDisconnected.Dispose();
    }
}

using System.Net.Sockets;
using System.Net;
using System.Text;
using ZXing.Net.Maui;

namespace MauiApp1;

public partial class MainPage : ContentPage
{
    private Socket? _socket;
    private bool _isConnected = false;
    private readonly object _lock = new();

    public MainPage()
    {
        InitializeComponent();

        // 初始化时加载上次的补光灯偏好
        bool isTorchOn = Preferences.Get("UserTorchPreference", false);
        TorchSwitch.IsToggled = isTorchOn;
        TorchStatusLabel.Text = isTorchOn ? "补光灯: 开" : "补光灯: 关";

        if (BarcodeReader != null)
        {
            BarcodeReader.Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode | BarcodeFormat.Code128 | BarcodeFormat.Ean13,
                AutoRotate = true,
                Multiple = false,
                TryHarder = true,
                TryInverted = true
            };
            BarcodeReader.CameraLocation = CameraLocation.Rear;
            BarcodeReader.CaptureQuality = CaptureQuality.High;
        }
        StartAutoConnectLoop();
    }

    private async void OnStartScanClicked(object sender, EventArgs e)
    {
        ScannerOverlay.IsVisible = true;
        await Task.Delay(300);
        BarcodeReader.IsDetecting = true;

        // 根据记录的偏好设置补光灯状态
        try
        {
            BarcodeReader.IsTorchOn = TorchSwitch.IsToggled;
        }
        catch { /* 设备不支持 */ }
    }

    private void OnTorchToggled(object sender, ToggledEventArgs e)
    {
        bool isOn = e.Value;

        // 1. 立即保存用户偏好
        Preferences.Set("UserTorchPreference", isOn);

        // 2. 更新UI文字
        TorchStatusLabel.Text = isOn ? "补光灯: 开" : "补光灯: 关";

        // 3. 如果当前正在扫描，立即切换物理灯光
        if (BarcodeReader != null && ScannerOverlay.IsVisible)
        {
            try
            {
                BarcodeReader.IsTorchOn = isOn;
            }
            catch { }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BarcodeReader != null)
        {
            BarcodeReader.IsDetecting = false;
            ScannerOverlay.IsVisible = false;
            try { BarcodeReader.IsTorchOn = false; } catch { }
        }
    }

    // --- 数值控制逻辑 ---
    private void OnPlusClicked(object sender, EventArgs e)
    {
        if (int.TryParse(CounterEntry.Text, out int val))
            CounterEntry.Text = (val + 1).ToString();
    }

    private void OnMinusClicked(object sender, EventArgs e)
    {
        if (int.TryParse(CounterEntry.Text, out int val) && val > 0)
            CounterEntry.Text = (val - 1).ToString();
    }

    private void OnCounterUnfocused(object sender, FocusEventArgs e)
    {
        if (!int.TryParse(CounterEntry.Text, out int val) || val < 0)
            CounterEntry.Text = "0";
    }

    // --- 自动重连逻辑 ---
    private void StartAutoConnectLoop()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                if (_isConnected)
                {
                    // 轮询检查 Socket 物理状态
                    if (_socket == null || !IsSocketConnected(_socket))
                    {
                        DisconnectSocket("服务器连接中断");
                    }
                }

                if (!_isConnected)
                {
                    // 检查手机是否有网络
                    if (Connectivity.Current.NetworkAccess != NetworkAccess.None &&
                        Connectivity.Current.NetworkAccess != NetworkAccess.Unknown)
                    {
                        await TryConnect();
                    }
                }

                await Task.Delay(3000);
            }
        });
    }

    private bool IsSocketConnected(Socket s)
    {
        try
        {
            return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
        }
        catch
        {
            return false;
        }
    }

    private async Task TryConnect()
    {
        string ip = Preferences.Get("IP", "192.168.6.6");
        if (!int.TryParse(Preferences.Get("Port", "5050"), out int port)) port = 8080;

        try
        {
            Socket tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connectTask = tempSocket.ConnectAsync(IPAddress.Parse(ip), port);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(2000));

            if (completedTask == connectTask && tempSocket.Connected)
            {
                lock (_lock) { _socket = tempSocket; _isConnected = true; }
                UpdateUIStatus(true);
            }
            else
            {
                tempSocket.Close();
                UpdateUIStatus(false);
            }
        }
        catch
        {
            UpdateUIStatus(false);
        }
    }

    private void DisconnectSocket(string reason)
    {
        lock (_lock)
        {
            if (_socket != null)
            {
                try { _socket.Close(); } catch { }
                _socket = null;
            }
            _isConnected = false;
        }
        UpdateUIStatus(false);
    }

    private void UpdateUIStatus(bool connected)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = connected ? "✅ 已连接服务器" : "❌ 未连接 (自动重试中...)";
            StatusFrame.BackgroundColor = connected ? Colors.Green : Colors.Red;
        });
    }

    // --- 扫码逻辑 ---
    private void OnStopScanClicked(object sender, EventArgs e)
    {
        BarcodeReader.IsDetecting = false;
        try { BarcodeReader.IsTorchOn = false; } catch { }
        ScannerOverlay.IsVisible = false;
    }

    private void OnBarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        var result = e.Results?.FirstOrDefault();
        if (result == null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            BarcodeReader.IsDetecting = false;
            ScannerOverlay.IsVisible = false;
            try { BarcodeReader.IsTorchOn = false; } catch { }
            try { Vibration.Default.Vibrate(); } catch { }
            // 直接发送原始扫码数据
            SendData(result.Value);
        });
    }

    private void SendData(string data)
    {
        // 再次检查连接状态
        if (!_isConnected || _socket == null)
        {
            AddLog($"❌ 未连接，丢弃: {data}", Colors.Red);
            DisconnectSocket("发送失败，判定为断开");
            return;
        }

        try
        {
            _socket.Send(Encoding.UTF8.GetBytes(data));
            AddLog($"📤 已发送: {data}", Colors.Black);

            // 发送成功后自动 +1
            MainThread.BeginInvokeOnMainThread(() => {
                if (int.TryParse(CounterEntry.Text, out int val))
                    CounterEntry.Text = (val + 1).ToString();
            });
        }
        catch
        {
            DisconnectSocket("发送异常");
            AddLog("❌ 连接已断开", Colors.Red);
        }
    }

    private void AddLog(string msg, Color color)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        LogEditor.Text = $"[{time}] {msg}\n" + LogEditor.Text;
    }

    private void OnClearLogClicked(object sender, EventArgs e) => LogEditor.Text = "";
}
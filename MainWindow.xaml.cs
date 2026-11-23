using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LanVoice.Audio;
using NAudio.CoreAudioApi;

namespace LanVoice;

public partial class MainWindow : Window
{
    private readonly LanAudioEngine _engine = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _isSharingWindow;

    public MainWindow()
    {
        InitializeComponent();
        LoadQualityPresets();
        LoadAudioDevices();
        LoadOpenWindows();
        LocalIpText.Text = $"Seu IP: {GetLocalLanIp()}";

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UpdateTelemetry;
        _uiTimer.Start();

        Closed += (_, _) => _engine.Dispose();
    }

    private void LoadQualityPresets()
    {
        var presets = new List<QualityOption>
        {
            new(AudioQualityPreset.VoiceMonoAppStereo, "Voz 48k Mono + App Estéreo (Recomendado)"),
            new(AudioQualityPreset.LowLatency24kMono, "Ultra-Leve 24kHz Mono (Menor Delay)"),
            new(AudioQualityPreset.Studio48kStereo, "Estúdio 48kHz Estéreo"),
            new(AudioQualityPreset.UltraLight16kMono, "Mínimo Tráfego 16kHz Mono")
        };
        QualityComboBox.ItemsSource = presets;
        QualityComboBox.SelectedIndex = 0;
    }

    private void LoadAudioDevices()
    {
        var mics = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var outputs = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        MicComboBox.ItemsSource = mics;
        OutputComboBox.ItemsSource = outputs;

        string? defaultMicId = null;
        string? defaultOutId = null;
        try { defaultMicId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }
        try { defaultOutId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

        MicComboBox.SelectedItem = mics.FirstOrDefault(d => d.ID == defaultMicId) ?? mics.FirstOrDefault();
        OutputComboBox.SelectedItem = outputs.FirstOrDefault(d => d.ID == defaultOutId) ?? outputs.FirstOrDefault();
    }

    private void LoadOpenWindows()
    {
        var list = new List<WindowItem>();
        int currentPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid) return true;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (!list.Any(w => w.ProcessId == (int)pid && w.Title == title))
                {
                    list.Add(new WindowItem((int)pid, proc.ProcessName, title));
                }
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        WindowComboBox.ItemsSource = list.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 0) WindowComboBox.SelectedIndex = 0;
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsConnected)
        {
            _engine.Stop();
            _isSharingWindow = false;
            UpdateConnectionUi(false);
            return;
        }

        if (MicComboBox.SelectedItem is not MMDevice mic || OutputComboBox.SelectedItem is not MMDevice output)
        {
            MessageBox.Show("Selecione um microfone e um dispositivo de saída.", "LanVoice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(LocalPortBox.Text, out int localPort) ||
            !int.TryParse(RemotePortBox.Text, out int remotePort) ||
            !int.TryParse(LatencyBox.Text, out int driverLatencyMs))
        {
            MessageBox.Show("Verifique as portas e o valor de Driver (ms).", "LanVoice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (QualityComboBox.SelectedItem is QualityOption q)
                _engine.Quality = q.Preset;

            _engine.TargetJitterMs = (int)JitterSlider.Value;
            _engine.Start(TargetIpBox.Text.Trim(), localPort, remotePort, mic, output, Math.Clamp(driverLatencyMs, 2, 50));
            ApplyVolumes();
            UpdateConnectionUi(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao iniciar áudio/rede: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ShareWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsConnected)
        {
            MessageBox.Show("Conecte-se ao outro computador primeiro.", "LanVoice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isSharingWindow)
        {
            _engine.StopWindowShare();
            _isSharingWindow = false;
            ShareWindowBtn.Content = "Compartilhar Janela";
            ShareWindowBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222631"));
            return;
        }

        if (WindowComboBox.SelectedItem is not WindowItem selected) return;

        try
        {
            await _engine.StartWindowShareAsync(selected.ProcessId);
            _isSharingWindow = true;
            string shortName = selected.Title.Length > 26 ? selected.Title[..26] + "..." : selected.Title;
            ShareWindowBtn.Content = $"Parar: {shortName}";
            ShareWindowBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#23A55A"));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível capturar esta janela:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityComboBox.SelectedItem is QualityOption q)
            _engine.Quality = q.Preset;
    }

    private void JitterSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        int ms = (int)JitterSlider.Value;
        _engine.TargetJitterMs = ms;
        JitterLabel.Text = $"{ms} ms";
    }

    private void JitterPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int ms))
            JitterSlider.Value = ms;
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => LoadOpenWindows();

    private void MuteMicBtn_Click(object sender, RoutedEventArgs e)
    {
        _engine.MicMuted = !_engine.MicMuted;
        MuteMicBtn.Content = _engine.MicMuted ? "🔇 Microfone Mutado" : "🎤 Microfone Ativo";
        MuteMicBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_engine.MicMuted ? "#DA373C" : "#222631"));
    }

    private void MuteAppBtn_Click(object sender, RoutedEventArgs e)
    {
        _engine.AppMuted = !_engine.AppMuted;
        MuteAppBtn.Content = _engine.AppMuted ? "App Mutado" : "Mutar App";
        MuteAppBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_engine.AppMuted ? "#DA373C" : "#222631"));
    }

    private void DeafenBtn_Click(object sender, RoutedEventArgs e)
    {
        _engine.Deafened = !_engine.Deafened;
        DeafenBtn.Content = _engine.Deafened ? "🔇 Surdo (Tudo Mutado)" : "🎧 Áudio Ativo";
        DeafenBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_engine.Deafened ? "#DA373C" : "#222631"));
    }

    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        ApplyVolumes();
    }

    private void ApplyVolumes()
    {
        _engine.LocalMicGain = (float)LocalMicSlider.Value;
        _engine.LocalAppGain = (float)LocalAppSlider.Value;
        _engine.RemoteMicVolume = (float)RemoteMicSlider.Value;
        _engine.RemoteAppVolume = (float)RemoteAppSlider.Value;

        LocalMicVolLabel.Text = $"{(int)(LocalMicSlider.Value * 100)}%";
        LocalAppVolLabel.Text = $"{(int)(LocalAppSlider.Value * 100)}%";
        RemoteMicVolLabel.Text = $"{(int)(RemoteMicSlider.Value * 100)}%";
        RemoteAppVolLabel.Text = $"{(int)(RemoteAppSlider.Value * 100)}%";
    }

    private void UpdateConnectionUi(bool connected)
    {
        ConnectBtn.Content = connected ? "Desconectar" : "Conectar";
        ConnectBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#DA373C" : "#5865F2"));
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#23A55A" : "#5C6370"));
        CallStatusText.Text = connected ? $"Conectado a {TargetIpBox.Text}" : "Desconectado";
        CallStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#23A55A" : "#8E96A8"));

        if (!connected)
        {
            ShareWindowBtn.Content = "Compartilhar Janela";
            ShareWindowBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#222631"));
            StatsText.Text = "Aguardando conexão...";
        }
    }

    private void UpdateTelemetry(object? sender, EventArgs e)
    {
        LocalMicVu.Value = Math.Min(100, _engine.LocalMicLevel * 140);
        LocalAppVu.Value = Math.Min(100, _engine.LocalAppLevel * 140);
        RemoteMicVu.Value = Math.Min(100, _engine.RemoteMicLevel * 140);
        RemoteAppVu.Value = Math.Min(100, _engine.RemoteAppLevel * 140);

        if (_engine.IsConnected)
            StatsText.Text = $"Buffer Mic: {_engine.RemoteMicBufferedMs} ms  •  Buffer App: {_engine.RemoteAppBufferedMs} ms  •  Pacotes: {_engine.PacketsReceived:N0}";
    }

    private static string GetLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public record WindowItem(int ProcessId, string ProcessName, string Title)
    {
        public override string ToString() => Title;
    }

    public record QualityOption(AudioQualityPreset Preset, string Label)
    {
        public override string ToString() => Label;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
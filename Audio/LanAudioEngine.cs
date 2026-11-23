using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanVoice.Audio;

public enum AudioQualityPreset
{
    VoiceMonoAppStereo, // Mic 48kHz Mono (1 pacote UDP) + App 48kHz Estéreo
    LowLatency24kMono,  // Mic e App 24kHz Mono (4x mais leve, ótimo p/ Wi-Fi)
    Studio48kStereo,    // Mic e App 48kHz Estéreo (máxima fidelidade)
    UltraLight16kMono   // Mic e App 16kHz Mono (tráfego mínimo)
}

public sealed class LanAudioEngine : IDisposable
{
    private const byte CHANNEL_MIC = 0x01;
    private const byte CHANNEL_APP = 0x02;

    private const byte FMT_48K_STEREO = 0x00;
    private const byte FMT_48K_MONO = 0x01;
    private const byte FMT_24K_MONO = 0x02;
    private const byte FMT_16K_MONO = 0x03;

    private const int MAX_UDP_AUDIO_CHUNK = 1200; // Múltiplo de 4 e de 2, abaixo do MTU 1500
    private const int BYTES_PER_MS_48K_STEREO = 192; // 48 frames/ms * 4 bytes

    private static readonly WaveFormat WireFormat = new(48000, 16, 2);
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private UdpClient? _udpClient;
    private IPEndPoint? _remoteEndPoint;
    private CancellationTokenSource? _cts;
    private Thread? _receiveThread;
    private bool _timerPeriodActive;

    private WasapiCapture? _micCapture;
    private ProcessLoopbackCapture? _appCapture;
    private WasapiOut? _wasapiOut;

    private BufferedWaveProvider? _remoteMicBuffer;
    private BufferedWaveProvider? _remoteAppBuffer;
    private VolumeSampleProvider? _remoteMicVol;
    private VolumeSampleProvider? _remoteAppVol;

    private short _lastMicSample;
    private short _lastAppSample;

    public bool IsConnected { get; private set; }
    public bool MicMuted { get; set; }
    public bool AppMuted { get; set; }
    public bool Deafened { get; set; }

    public AudioQualityPreset Quality { get; set; } = AudioQualityPreset.VoiceMonoAppStereo;
    public int TargetJitterMs { get; set; } = 8; // Ajustável em tempo real (2 a 60 ms)

    public float LocalMicGain { get; set; } = 1.0f;
    public float LocalAppGain { get; set; } = 1.0f;

    public float RemoteMicVolume
    {
        get => _remoteMicVol?.Volume ?? 1.0f;
        set { if (_remoteMicVol != null) _remoteMicVol.Volume = value; }
    }

    public float RemoteAppVolume
    {
        get => _remoteAppVol?.Volume ?? 1.0f;
        set { if (_remoteAppVol != null) _remoteAppVol.Volume = value; }
    }

    public float LocalMicLevel { get; private set; }
    public float LocalAppLevel { get; private set; }
    public float RemoteMicLevel { get; private set; }
    public float RemoteAppLevel { get; private set; }
    public long PacketsReceived { get; private set; }

    public int RemoteMicBufferedMs => (int)(_remoteMicBuffer?.BufferedDuration.TotalMilliseconds ?? 0);
    public int RemoteAppBufferedMs => (int)(_remoteAppBuffer?.BufferedDuration.TotalMilliseconds ?? 0);

    public void Start(string targetIp, int localPort, int remotePort, MMDevice micDevice, MMDevice outputDevice, int driverLatencyMs = 5)
    {
        Stop();

        if (timeBeginPeriod(1) == 0)
            _timerPeriodActive = true;

        _lastMicSample = 0;
        _lastAppSample = 0;

        _remoteEndPoint = new IPEndPoint(IPAddress.Parse(targetIp), remotePort);
        _udpClient = new UdpClient(localPort);
        _udpClient.Client.ReceiveBufferSize = 65536;
        _udpClient.Client.SendBufferSize = 65536;

        // 1. Saída de áudio (Mixagem de Mic Remoto + App Remoto)
        _remoteMicBuffer = new BufferedWaveProvider(WireFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromMilliseconds(250)
        };

        _remoteAppBuffer = new BufferedWaveProvider(WireFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromMilliseconds(250)
        };

        _remoteMicVol = new VolumeSampleProvider(_remoteMicBuffer.ToSampleProvider()) { Volume = 1.0f };
        _remoteAppVol = new VolumeSampleProvider(_remoteAppBuffer.ToSampleProvider()) { Volume = 1.0f };

        ISampleProvider finalOutput = new MixingSampleProvider(new ISampleProvider[] { _remoteMicVol, _remoteAppVol })
        {
            ReadFully = true
        };

        int deviceSampleRate = 48000;
        try
        {
            using var tempClient = outputDevice.AudioClient;
            deviceSampleRate = tempClient.MixFormat.SampleRate;
        }
        catch { }

        if (deviceSampleRate != 48000)
        {
            finalOutput = new WdlResamplingSampleProvider(finalOutput, deviceSampleRate);
        }

        _wasapiOut = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: Math.Clamp(driverLatencyMs, 2, 50));
        _wasapiOut.Init(finalOutput);
        _wasapiOut.Play();

        // 2. Captura do Microfone Local (menor período suportado pelo driver)
        _micCapture = new WasapiCapture(micDevice, useEventSync: true, audioBufferMillisecondsLength: Math.Clamp(driverLatencyMs, 2, 20));
        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.StartRecording();

        // 3. Thread dedicada de alta prioridade para recepção UDP (evita jitter do ThreadPool)
        _cts = new CancellationTokenSource();
        PacketsReceived = 0;
        IsConnected = true;

        var token = _cts.Token;
        _receiveThread = new Thread(() => ReceiveLoop(token))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "LanVoiceUdpReceiver"
        };
        _receiveThread.Start();
    }

    public async Task StartWindowShareAsync(int processId)
    {
        StopWindowShare();
        if (!IsConnected) return;

        _appCapture = new ProcessLoopbackCapture();
        _appCapture.DataAvailable += OnAppDataAvailable;
        await _appCapture.StartAsync(processId);
    }

    public void StopWindowShare()
    {
        if (_appCapture != null)
        {
            _appCapture.DataAvailable -= OnAppDataAvailable;
            _appCapture.Dispose();
            _appCapture = null;
        }
        LocalAppLevel = 0;
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _micCapture == null) return;
        if (MicMuted || Deafened)
        {
            LocalMicLevel = 0;
            return;
        }

        byte formatId = Quality switch
        {
            AudioQualityPreset.Studio48kStereo => FMT_48K_STEREO,
            AudioQualityPreset.LowLatency24kMono => FMT_24K_MONO,
            AudioQualityPreset.UltraLight16kMono => FMT_16K_MONO,
            _ => FMT_48K_MONO
        };

        byte[] encoded = EncodeMicToPcm16(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat, formatId, LocalMicGain, out float peak);
        LocalMicLevel = peak;

        if (encoded.Length > 0)
            SendAudioPacket(CHANNEL_MIC, formatId, encoded, encoded.Length);
    }

    private void OnAppDataAvailable(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded == 0 || AppMuted)
        {
            LocalAppLevel = 0;
            return;
        }

        byte formatId = Quality switch
        {
            AudioQualityPreset.LowLatency24kMono => FMT_24K_MONO,
            AudioQualityPreset.UltraLight16kMono => FMT_16K_MONO,
            _ => FMT_48K_STEREO
        };

        byte[] encoded = EncodeApp48kStereo(buffer, bytesRecorded, formatId, LocalAppGain, out float peak);
        LocalAppLevel = peak;

        if (encoded.Length > 0)
            SendAudioPacket(CHANNEL_APP, formatId, encoded, encoded.Length);
    }

    private void SendAudioPacket(byte channelId, byte formatId, byte[] audioData, int length)
    {
        if (_udpClient == null || _remoteEndPoint == null) return;

        int offset = 0;
        while (offset < length)
        {
            int chunk = Math.Min(MAX_UDP_AUDIO_CHUNK, length - offset);
            byte[] packet = new byte[chunk + 2];
            packet[0] = channelId;
            packet[1] = formatId;
            Buffer.BlockCopy(audioData, offset, packet, 2, chunk);

            try { _udpClient.Send(packet, packet.Length, _remoteEndPoint); } catch { }
            offset += chunk;
        }
    }

    private void ReceiveLoop(CancellationToken token)
    {
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
        byte[] discardScratch = new byte[4096];

        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remoteEp);
                if (data.Length <= 2) continue;

                PacketsReceived++;
                if (Deafened)
                {
                    RemoteMicLevel = 0;
                    RemoteAppLevel = 0;
                    continue;
                }

                byte channel = data[0];
                byte formatId = data[1];
                int audioLen = data.Length - 2;

                if (channel == CHANNEL_MIC && _remoteMicBuffer != null)
                {
                    byte[] pcm48k = DecodeToPcm48kStereo(data, 2, audioLen, formatId, ref _lastMicSample, out float peak);
                    EnqueueWithLowLatencyTrim(_remoteMicBuffer, pcm48k, discardScratch);
                    RemoteMicLevel = peak;
                }
                else if (channel == CHANNEL_APP && _remoteAppBuffer != null)
                {
                    byte[] pcm48k = DecodeToPcm48kStereo(data, 2, audioLen, formatId, ref _lastAppSample, out float peak);
                    EnqueueWithLowLatencyTrim(_remoteAppBuffer, pcm48k, discardScratch);
                    RemoteAppLevel = peak;
                }
            }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private void EnqueueWithLowLatencyTrim(BufferedWaveProvider buffer, byte[] pcm48kStereo, byte[] scratch)
    {
        if (pcm48kStereo.Length == 0) return;

        int targetMs = Math.Clamp(TargetJitterMs, 2, 60);
        int targetBytes = targetMs * BYTES_PER_MS_48K_STEREO;
        int maxQueueBeforeAdd = targetBytes + (6 * BYTES_PER_MS_48K_STEREO);

        // Mantém a fila colada ao alvo sem apagar o buffer inteiro (evita cortes e acúmulo de delay)
        if (buffer.BufferedBytes > maxQueueBeforeAdd)
        {
            int bytesToDrop = buffer.BufferedBytes - targetBytes;
            bytesToDrop -= bytesToDrop % 4;

            while (bytesToDrop > 0)
            {
                int chunk = Math.Min(bytesToDrop, scratch.Length);
                chunk -= chunk % 4;
                if (chunk <= 0) break;

                int read = buffer.Read(scratch, 0, chunk);
                if (read <= 0) break;
                bytesToDrop -= read;
            }
        }

        buffer.AddSamples(pcm48kStereo, 0, pcm48kStereo.Length);
    }

    private static bool IsFloatFormat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        if (fmt is WaveFormatExtensible wfe)
            return wfe.SubFormat == SubtypeIeeeFloat;
        return fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32;
    }

    private static float ReadSample(byte[] src, int offset, bool isFloat, int bitsPerSample)
    {
        if (isFloat)
            return BitConverter.ToSingle(src, offset);

        return bitsPerSample switch
        {
            16 => BitConverter.ToInt16(src, offset) / 32768f,
            24 => ((sbyte)src[offset + 2] << 16 | src[offset + 1] << 8 | src[offset]) / 8388608f,
            32 => BitConverter.ToInt32(src, offset) / 2147483648f,
            _ => 0f
        };
    }

    private static byte[] EncodeMicToPcm16(byte[] src, int bytesRecorded, WaveFormat fmt, byte formatId, float gain, out float peak)
    {
        peak = 0;
        int channels = fmt.Channels;
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (channels <= 0 || bytesPerSample <= 0) return Array.Empty<byte>();

        int srcFrames = bytesRecorded / (channels * bytesPerSample);
        if (srcFrames == 0) return Array.Empty<byte>();

        int targetRate = formatId switch
        {
            FMT_24K_MONO => 24000,
            FMT_16K_MONO => 16000,
            _ => 48000
        };
        bool stereoOut = formatId == FMT_48K_STEREO;

        double ratio = (double)fmt.SampleRate / targetRate;
        int dstFrames = (int)(srcFrames / ratio);
        if (dstFrames <= 0) return Array.Empty<byte>();

        bool isFloat = IsFloatFormat(fmt);
        byte[] dst = new byte[dstFrames * (stereoOut ? 4 : 2)];

        for (int i = 0; i < dstFrames; i++)
        {
            int srcIdx = Math.Min((int)(i * ratio), srcFrames - 1);
            int byteOffset = srcIdx * channels * bytesPerSample;

            float left = ReadSample(src, byteOffset, isFloat, fmt.BitsPerSample);
            float right = channels > 1
                ? ReadSample(src, byteOffset + bytesPerSample, isFloat, fmt.BitsPerSample)
                : left;

            if (stereoOut)
            {
                left = Math.Clamp(left * gain, -1f, 1f);
                right = Math.Clamp(right * gain, -1f, 1f);
                float abs = Math.Max(Math.Abs(left), Math.Abs(right));
                if (abs > peak) peak = abs;

                short sL = (short)(left * 32767f);
                short sR = (short)(right * 32767f);
                int dstOffset = i * 4;
                dst[dstOffset] = (byte)(sL & 0xFF);
                dst[dstOffset + 1] = (byte)((sL >> 8) & 0xFF);
                dst[dstOffset + 2] = (byte)(sR & 0xFF);
                dst[dstOffset + 3] = (byte)((sR >> 8) & 0xFF);
            }
            else
            {
                float mono = Math.Clamp(((left + right) * 0.5f) * gain, -1f, 1f);
                float abs = Math.Abs(mono);
                if (abs > peak) peak = abs;

                short sM = (short)(mono * 32767f);
                int dstOffset = i * 2;
                dst[dstOffset] = (byte)(sM & 0xFF);
                dst[dstOffset + 1] = (byte)((sM >> 8) & 0xFF);
            }
        }

        return dst;
    }

    private static byte[] EncodeApp48kStereo(byte[] buffer, int bytesRecorded, byte formatId, float gain, out float peak)
    {
        peak = 0;
        int frames = bytesRecorded / 4;
        if (frames == 0) return Array.Empty<byte>();

        if (formatId == FMT_48K_STEREO)
        {
            byte[] dst = new byte[frames * 4];
            for (int i = 0; i < frames * 4; i += 2)
            {
                float scaled = Math.Clamp(BitConverter.ToInt16(buffer, i) * gain, -32768f, 32767f);
                short s = (short)scaled;
                dst[i] = (byte)(s & 0xFF);
                dst[i + 1] = (byte)((s >> 8) & 0xFF);

                float abs = Math.Abs(scaled) / 32768f;
                if (abs > peak) peak = abs;
            }
            return dst;
        }

        int decimation = formatId == FMT_16K_MONO ? 3 : 2;
        int outSamples = frames / decimation;
        byte[] monoDst = new byte[outSamples * 2];

        for (int i = 0; i < outSamples; i++)
        {
            float sum = 0;
            for (int d = 0; d < decimation; d++)
            {
                int idx = (i * decimation + d) * 4;
                short l = BitConverter.ToInt16(buffer, idx);
                short r = BitConverter.ToInt16(buffer, idx + 2);
                sum += (l + r) * 0.5f;
            }

            float scaled = Math.Clamp((sum / decimation) * gain, -32768f, 32767f);
            short s = (short)scaled;
            monoDst[i * 2] = (byte)(s & 0xFF);
            monoDst[i * 2 + 1] = (byte)((s >> 8) & 0xFF);

            float abs = Math.Abs(scaled) / 32768f;
            if (abs > peak) peak = abs;
        }

        return monoDst;
    }

    private static byte[] DecodeToPcm48kStereo(byte[] packet, int offset, int audioLen, byte formatId, ref short lastSample, out float peak)
    {
        peak = 0;

        if (formatId == FMT_48K_STEREO)
        {
            int len = audioLen - (audioLen % 4);
            byte[] dst = new byte[len];
            Buffer.BlockCopy(packet, offset, dst, 0, len);
            for (int i = 0; i < len; i += 2)
            {
                float abs = Math.Abs(BitConverter.ToInt16(dst, i)) / 32768f;
                if (abs > peak) peak = abs;
            }
            return dst;
        }

        int samples = audioLen / 2;
        if (samples <= 0) return Array.Empty<byte>();

        if (formatId == FMT_48K_MONO)
        {
            byte[] dst = new byte[samples * 4];
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(packet, offset + i * 2);
                float abs = Math.Abs(s) / 32768f;
                if (abs > peak) peak = abs;
                WriteStereoFrame(dst, i * 4, s);
            }
            return dst;
        }

        if (formatId == FMT_24K_MONO)
        {
            byte[] dst = new byte[samples * 8];
            for (int i = 0; i < samples; i++)
            {
                short curr = BitConverter.ToInt16(packet, offset + i * 2);
                float abs = Math.Abs(curr) / 32768f;
                if (abs > peak) peak = abs;

                short mid = (short)((lastSample + curr) / 2);
                WriteStereoFrame(dst, i * 8, mid);
                WriteStereoFrame(dst, i * 8 + 4, curr);
                lastSample = curr;
            }
            return dst;
        }

        // FMT_16K_MONO (upsample 3x com interpolação linear sem latência)
        byte[] dst16 = new byte[samples * 12];
        for (int i = 0; i < samples; i++)
        {
            short curr = BitConverter.ToInt16(packet, offset + i * 2);
            float abs = Math.Abs(curr) / 32768f;
            if (abs > peak) peak = abs;

            short s1 = (short)((2 * lastSample + curr) / 3);
            short s2 = (short)((lastSample + 2 * curr) / 3);
            WriteStereoFrame(dst16, i * 12, s1);
            WriteStereoFrame(dst16, i * 12 + 4, s2);
            WriteStereoFrame(dst16, i * 12 + 8, curr);
            lastSample = curr;
        }
        return dst16;
    }

    private static void WriteStereoFrame(byte[] dst, int offset, short sample)
    {
        byte lo = (byte)(sample & 0xFF);
        byte hi = (byte)((sample >> 8) & 0xFF);
        dst[offset] = lo;
        dst[offset + 1] = hi;
        dst[offset + 2] = lo;
        dst[offset + 3] = hi;
    }

    public void Stop()
    {
        IsConnected = false;
        StopWindowShare();
        _cts?.Cancel();

        try { _udpClient?.Close(); } catch { }
        _udpClient?.Dispose();
        _udpClient = null;

        if (_receiveThread != null && _receiveThread.IsAlive)
            _receiveThread.Join(300);
        _receiveThread = null;

        if (_micCapture != null)
        {
            _micCapture.DataAvailable -= OnMicDataAvailable;
            try { _micCapture.StopRecording(); } catch { }
            _micCapture.Dispose();
            _micCapture = null;
        }

        if (_wasapiOut != null)
        {
            try { _wasapiOut.Stop(); } catch { }
            _wasapiOut.Dispose();
            _wasapiOut = null;
        }

        _cts?.Dispose();
        _cts = null;

        if (_timerPeriodActive)
        {
            timeEndPeriod(1);
            _timerPeriodActive = false;
        }

        LocalMicLevel = LocalAppLevel = RemoteMicLevel = RemoteAppLevel = 0;
    }

    public void Dispose() => Stop();

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
}
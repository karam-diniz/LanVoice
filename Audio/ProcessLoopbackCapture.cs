using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LanVoice.Audio;

public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    private const uint AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const uint PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const uint AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private CancellationTokenSource? _cts;
    private Thread? _workerThread;

    public event Action<byte[], int>? DataAvailable;

    public Task StartAsync(int processId)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _cts.Token;

        _workerThread = new Thread(() => RunOnMtaThread(processId, token, startupTcs))
        {
            IsBackground = true,
            Name = "ProcessLoopbackMTA"
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();

        return startupTcs.Task;
    }

    private void RunOnMtaThread(int processId, CancellationToken token, TaskCompletionSource<bool> startupTcs)
    {
        IActivateAudioInterfaceAsyncOperation? asyncOp = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        AutoResetEvent? sampleReadyEvent = null;

        try
        {
            var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = (uint)processId,
                    ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                }
            };

            using var handler = new ActivationCompletionHandler();
            IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
            try
            {
                Marshal.StructureToPtr(activationParams, paramsPtr, false);

                var propVariant = new PropVariantBlob
                {
                    vt = 65, // VT_BLOB
                    cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                    pBlobData = paramsPtr
                };

                Guid iidAudioClient = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync(
                    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    ref iidAudioClient,
                    ref propVariant,
                    handler,
                    out asyncOp);

                Marshal.ThrowExceptionForHR(hr);

                if (!handler.Wait(5000))
                    throw new TimeoutException("Tempo esgotado ao ativar a captura de áudio do processo.");

                if (handler.Error != null)
                    throw handler.Error;

                audioClient = handler.Client ?? throw new InvalidOperationException("IAudioClient nulo.");
            }
            finally
            {
                Marshal.FreeHGlobal(paramsPtr);
            }

            var wfx = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 16,
                nBlockAlign = 4,
                nAvgBytesPerSec = 48000 * 4,
                cbSize = 0
            };

            IntPtr wfxPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            try
            {
                Marshal.StructureToPtr(wfx, wfxPtr, false);

                uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                             AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

                int hrInit = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, wfxPtr, IntPtr.Zero);
                if (hrInit != 0)
                {
                    // Fallback para builds do Windows que não aceitam AUTOCONVERTPCM no Process Loopback
                    hrInit = audioClient.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                        0, 0, wfxPtr, IntPtr.Zero);
                }
                Marshal.ThrowExceptionForHR(hrInit);
            }
            finally
            {
                Marshal.FreeHGlobal(wfxPtr);
            }

            sampleReadyEvent = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(sampleReadyEvent.SafeWaitHandle.DangerousGetHandle()));

            Guid iidCapture = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCapture, out object captureObj));
            captureClient = (IAudioCaptureClient)captureObj;

            Marshal.ThrowExceptionForHR(audioClient.Start());
            startupTcs.TrySetResult(true);

            byte[] buffer = new byte[19200];
            while (!token.IsCancellationRequested)
            {
                if (!sampleReadyEvent.WaitOne(50)) continue;

                while (!token.IsCancellationRequested)
                {
                    int hr = captureClient.GetNextPacketSize(out uint packetFrames);
                    if (hr != 0 || packetFrames == 0) break;

                    hr = captureClient.GetBuffer(out IntPtr dataPtr, out uint framesToRead, out uint flags, out _, out _);
                    if (hr != 0) break;

                    try
                    {
                        int byteCount = (int)framesToRead * 4;
                        if (byteCount > 0)
                        {
                            if (buffer.Length < byteCount) buffer = new byte[byteCount];

                            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            {
                                Array.Clear(buffer, 0, byteCount);
                            }
                            else
                            {
                                Marshal.Copy(dataPtr, buffer, 0, byteCount);
                                DataAvailable?.Invoke(buffer, byteCount);
                            }
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(framesToRead);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            startupTcs.TrySetException(ex);
        }
        finally
        {
            try { audioClient?.Stop(); } catch { }
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            if (asyncOp != null) Marshal.ReleaseComObject(asyncOp);
            sampleReadyEvent?.Dispose();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(500);
        }
        _workerThread = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    #region COM Interop

    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public uint ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormatPointer);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject, IDisposable
    {
        private readonly ManualResetEventSlim _done = new(false);

        public IAudioClient? Client { get; private set; }
        public Exception? Error { get; private set; }

        public bool Wait(int timeoutMs) => _done.Wait(timeoutMs);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activated);
                if (hr != 0) Error = Marshal.GetExceptionForHR(hr);
                else Client = (IAudioClient)activated;
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                _done.Set();
            }
        }

        public void Dispose() => _done.Dispose();
    }

    #endregion
}
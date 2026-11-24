using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpireReplay.Media;

[SupportedOSPlatform("windows10.0.20348")]
internal static class AudioInterfaces
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        internal ushort Tag, Channels;
        internal uint Rate, BytesPerSecond;
        internal ushort Align, Bits, Extra;
    }

    // PROPVARIANT containing a VT_BLOB on the game's Win64 target.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ActivationVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal uint Size;
        [FieldOffset(16)] internal IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(string device, in Guid iid, ref ActivationVariant variant,
        IActivationHandler handler, out IActivationOperation operation);

    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
    public interface IActivationHandler
    {
        [PreserveSig] int ActivateCompleted(IActivationOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    private sealed class Handler : IActivationHandler
    {
        internal TaskCompletionSource<object> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActivateCompleted(IActivationOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var result, out var instance));
                Marshal.ThrowExceptionForHR(result);
                Result.TrySetResult(instance);
            }
            catch (Exception e) { Result.TrySetException(e); }
            return 0;
        }
    }

    internal static IAudioClient Activate()
    {
        var data = Marshal.AllocCoTaskMem(12);
        IActivationOperation? operation = null;
        try
        {
            Marshal.WriteInt32(data, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(data, 4, Environment.ProcessId);
            Marshal.WriteInt32(data, 8, 0); // INCLUDE_TARGET_PROCESS_TREE
            var variant = new ActivationVariant { Type = 65, Size = 12, Data = data };
            var handler = new Handler();
            var iid = typeof(IAudioClient).GUID;
            var hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in iid, ref variant, handler, out operation);
            Marshal.ThrowExceptionForHR(hr);
            var result = handler.Result.Task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            GC.KeepAlive(handler);
            return (IAudioClient)result;
        }
        finally
        {
            if (operation != null) Marshal.ReleaseComObject(operation);
            Marshal.FreeCoTaskMem(data);
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr buffer, out uint frames, out uint flags, out ulong position, out ulong qpc100ns);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

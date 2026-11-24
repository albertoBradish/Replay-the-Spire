using System.Runtime.InteropServices;
using System.Text;

namespace ReplayHarness;

/// <summary>In-process timing signal, mixed only to the Steam virtual speakers.</summary>
internal sealed class SyncPulse : IDisposable
{
    internal const int Frequency = 2300;
    internal const string Output = "Steam Streaming Speakers";
    private nint system, oscillator, channel;

    internal static SyncPulse Open()
    {
        var pulse = new SyncPulse();
        try
        {
            Check(FMOD_System_Create(out pulse.system, 0x00020306));
            Check(FMOD_System_GetNumDrivers(pulse.system, out int count));
            var matches = new List<int>();
            for (int index = 0; index < count; index++)
            {
                var name = new StringBuilder(512);
                Check(FMOD_System_GetDriverInfo(pulse.system, index, name, name.Capacity, 0, out _, out _, out _));
                if (name.ToString().Contains(Output, StringComparison.OrdinalIgnoreCase)) matches.Add(index);
            }
            if (matches.Count != 1) throw new InvalidOperationException("Virtual audio output is unavailable or ambiguous.");
            Check(FMOD_System_SetDriver(pulse.system, matches[0]));
            Check(FMOD_System_SetDSPBufferSize(pulse.system, 256, 4));
            Check(FMOD_System_Init(pulse.system, 8, 0, 0));
            Check(FMOD_System_GetDriver(pulse.system, out int active));
            if (active != matches[0]) throw new InvalidOperationException("Test pulse routed to unexpected output.");
            Check(FMOD_System_CreateDSPByType(pulse.system, 2, out pulse.oscillator)); // FMOD_DSP_TYPE_OSCILLATOR
            Check(FMOD_DSP_SetParameterFloat(pulse.oscillator, 1, Frequency));
            Check(FMOD_System_PlayDSP(pulse.system, pulse.oscillator, 0, 1, out pulse.channel));
            Check(FMOD_Channel_SetVolume(pulse.channel, .3f));
            return pulse;
        }
        catch { pulse.Dispose(); throw; }
    }

    internal void Set(bool on)
    {
        Check(FMOD_Channel_SetPaused(channel, on ? 0 : 1));
        Check(FMOD_System_Update(system));
    }

    public void Dispose()
    {
        if (channel != 0) { FMOD_Channel_Stop(channel); channel = 0; }
        if (oscillator != 0) { FMOD_DSP_Release(oscillator); oscillator = 0; }
        if (system != 0) { FMOD_System_Release(system); system = 0; }
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"FMOD test pulse failed ({result}).");
    }

    [DllImport("fmod.dll")] static extern int FMOD_System_Create(out nint system, uint version);
    [DllImport("fmod.dll")] static extern int FMOD_System_GetNumDrivers(nint system, out int count);
    [DllImport("fmod.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern int FMOD_System_GetDriverInfo(nint system, int index, StringBuilder name, int length, nint guid, out int rate, out int mode, out int channels);
    [DllImport("fmod.dll")] static extern int FMOD_System_SetDriver(nint system, int index);
    [DllImport("fmod.dll")] static extern int FMOD_System_GetDriver(nint system, out int index);
    [DllImport("fmod.dll")] static extern int FMOD_System_SetDSPBufferSize(nint system, uint length, int count);
    [DllImport("fmod.dll")] static extern int FMOD_System_Init(nint system, int count, uint flags, nint extra);
    [DllImport("fmod.dll")] static extern int FMOD_System_CreateDSPByType(nint system, int type, out nint dsp);
    [DllImport("fmod.dll")] static extern int FMOD_DSP_SetParameterFloat(nint dsp, int index, float value);
    [DllImport("fmod.dll")] static extern int FMOD_System_PlayDSP(nint system, nint dsp, nint group, int paused, out nint channel);
    [DllImport("fmod.dll")] static extern int FMOD_Channel_SetVolume(nint channel, float value);
    [DllImport("fmod.dll")] static extern int FMOD_Channel_SetPaused(nint channel, int value);
    [DllImport("fmod.dll")] static extern int FMOD_System_Update(nint system);
    [DllImport("fmod.dll")] static extern int FMOD_Channel_Stop(nint channel);
    [DllImport("fmod.dll")] static extern int FMOD_DSP_Release(nint dsp);
    [DllImport("fmod.dll")] static extern int FMOD_System_Release(nint system);
}

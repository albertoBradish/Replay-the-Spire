using Godot;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Saves;

namespace ReplayHarness;

internal sealed class AudioTestOutput : IDisposable
{
    private readonly GodotObject server;
    private readonly NAudioManager audio;
    private readonly SettingsSave settings;
    private readonly int previousDriver;
    private readonly float previousGain;
    private bool disposed;

    private AudioTestOutput(GodotObject server, NAudioManager audio)
    {
        this.server = server;
        this.audio = audio;
        previousDriver = server.Call("get_driver").AsInt32();
        settings = SaveManager.Instance.SettingsSave;
        previousGain = settings.VolumeMaster;
    }

    internal static AudioTestOutput Open(bool audible = true)
    {
        if (!OS.GetUserDataDir().Contains("ReplayAcceptance-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance profile is required for virtual audio routing.");
        var server = Engine.GetSingleton("FmodServer") ?? throw new InvalidOperationException("FMOD server unavailable.");
        var audio = NAudioManager.Instance ?? throw new InvalidOperationException("Game audio unavailable.");
        var available = server.Call("get_available_drivers").AsGodotArray();
        var matches = available.Select(v => v.AsGodotDictionary())
            .Where(d => d["name"].AsString().Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The Steam Streaming Speakers virtual driver was not found.");
        var route = new AudioTestOutput(server, audio);
        if (route.previousDriver < 0) throw new InvalidOperationException("Could not determine the original FMOD output.");
        try
        {
            int driver = matches[0]["id"].AsInt32();
            server.Call("set_driver", driver);
            if (server.Call("get_driver").AsInt32() != driver) throw new InvalidOperationException("FMOD virtual routing did not apply.");
            route.settings.VolumeMaster = audible ? 1f : 0f;
            audio.SetMasterVol(route.settings.VolumeMaster);
            GD.Print("[ReplayHarness] FMOD virtual audio output active");
            return route;
        }
        catch { route.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            settings.VolumeMaster = previousGain;
            audio.SetMasterVol(previousGain);
            server.Call("update");
        }
        finally { server.Call("set_driver", previousDriver); }
    }
}

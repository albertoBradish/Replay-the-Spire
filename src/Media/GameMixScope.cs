using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Saves;

namespace SpireReplay.Media;

/// <summary>Keeps the game mix at its export gain while the progress window takes focus.</summary>
internal sealed class GameMixScope : IDisposable
{
    private static readonly Harmony Patches = new("io.github.albertobradish.replaythespire.exportaudio");
    private static GameMixScope? active;
    private readonly List<(MethodInfo Target, MethodInfo Prefix)> patched = [];
    private readonly NGame game;
    private readonly float gain;
    private float restoreFmod, restoreGodot;
    private bool applying;

    private GameMixScope(NGame game, float? requestedGain)
    {
        this.game = game;
        var settings = SaveManager.Instance.SettingsSave;
        gain = requestedGain ?? settings.VolumeMaster;
        if (!float.IsFinite(gain) || gain is < 0f or > 1f) throw new ArgumentOutOfRangeException(nameof(requestedGain));
        bool focused = DisplayServer.GetWindowList().Any(id => DisplayServer.WindowIsFocused(id));
        float fallback = !focused && SaveManager.Instance.PrefsSave.MuteInBackground ? 0f : settings.VolumeMaster;
        var server = Engine.GetSingleton("FmodServer");
        var bus = server?.Call("get_bus", "bus:/master").AsGodotObject();
        var volume = bus?.Get("volume");
        restoreFmod = volume is { VariantType: Variant.Type.Float } v ? Mathf.Sqrt(Mathf.Max(0, v.AsSingle())) : fallback;
        int master = AudioServer.GetBusIndex("Master");
        restoreGodot = master >= 0 ? Mathf.Sqrt(Mathf.DbToLinear(AudioServer.GetBusVolumeDb(master))) : fallback;
    }

    internal static GameMixScope Open(float? requestedGain = null)
    {
        if (active != null) throw new InvalidOperationException("导出音量已启用。");
        var game = NGame.Instance ?? throw new InvalidOperationException("游戏音频尚未就绪。");
        var scope = new GameMixScope(game, requestedGain);
        active = scope;
        try
        {
            scope.Hold(typeof(NAudioManager), nameof(NAudioManager.SetMasterVol), nameof(KeepFmod));
            scope.Hold(typeof(NDebugAudioManager), nameof(NDebugAudioManager.SetMasterAudioVolume), nameof(KeepGodot));
            scope.applying = true;
            scope.game.AudioManager.SetMasterVol(scope.gain);
            scope.game.DebugAudio.SetMasterAudioVolume(scope.gain);
            scope.applying = false;
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private void Hold(Type type, string method, string prefixName)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        var prefix = AccessTools.DeclaredMethod(typeof(GameMixScope), prefixName);
        Patches.Patch(target, prefix: new HarmonyMethod(prefix));
        patched.Add((target, prefix));
    }

    // Let native focus tweens advance and retain the most recent levels requested by the game.
    private static void KeepFmod(ref float __0)
    {
        if (active is not { applying: false } scope) return;
        scope.restoreFmod = __0;
        __0 = scope.gain;
    }

    private static void KeepGodot(ref float __0)
    {
        if (active is not { applying: false } scope) return;
        scope.restoreGodot = __0;
        __0 = scope.gain;
    }

    public void Dispose()
    {
        if (active != this) return;
        active = null;
        try
        {
            foreach (var (target, prefix) in patched) Patches.Unpatch(target, prefix);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game))
            {
                game.AudioManager.SetMasterVol(restoreFmod);
                game.DebugAudio.SetMasterAudioVolume(restoreGodot);
            }
        }
    }
}

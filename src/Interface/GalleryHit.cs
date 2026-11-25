using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using SpireReplay.Playback;

namespace SpireReplay.Interface;

// A damage number can already be gone by the time the native action queue reaches its next boundary.
internal sealed class GalleryHit : IDisposable
{
    private static GalleryHit? active;
    private static readonly System.Reflection.FieldInfo FlyingCard = AccessTools.Field(typeof(NCardFlyVfx), "_card");
    private readonly SceneTree tree;
    private readonly Gallery.CardMoment moment;
    private readonly Func<Action> hideOverlays;
    private NDamageNumVfx? damage;
    private ulong drawAfter;
    private NCard? playedCard;
    private NCard? flyingCard;
    private Image? image;
    private Action? restore;
    private Exception? failure;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NDamageNumVfx), nameof(NDamageNumVfx.Create), [typeof(Creature), typeof(DamageResult)]),
            postfix: new HarmonyMethod(typeof(GalleryHit), nameof(DamageCreated)));
        harmony.Patch(AccessTools.Method(typeof(NCardFlyVfx), nameof(NCardFlyVfx._Ready)),
            postfix: new HarmonyMethod(typeof(GalleryHit), nameof(CardMoving)));
    }

    internal GalleryHit(SceneTree tree, Gallery.CardMoment moment, Func<Action> hideOverlays)
    {
        this.tree = tree;
        this.moment = moment;
        this.hideOverlays = hideOverlays;
        active = this;
        RenderingServer.FramePreDraw += PrepareFrame;
        RenderingServer.FramePostDraw += ReadFrame;
    }

    private bool AtDecision => ReplayPlayer.CurrentCombatDecision == moment.Decision;
    private bool Matches(NCard card) => card.Model?.Id.ToString() == moment.Card;

    private static void DamageCreated(Creature target, DamageResult result, NDamageNumVfx? __result)
    {
        if (active is not { image: null, failure: null } shot || !shot.AtDecision || __result == null ||
            target.Side != CombatSide.Enemy || result.UnblockedDamage <= 0)
            return;
        var room = NCombatRoom.Instance;
        shot.damage = __result;
        // The game has applied the damage, but it defers refreshing the creature's UI.
        var healthBar = room?.GetCreatureNode(target)?.GetNodeOrNull<NCreatureStateDisplay>("%HealthBar")
            ?.GetNodeOrNull<NHealthBar>("%HealthBar");
        healthBar?.RefreshValues();
        shot.drawAfter = Engine.GetProcessFrames() + 1;
        shot.playedCard = room?.Ui.PlayContainer.GetChildren().OfType<NCard>().FirstOrDefault(shot.Matches);
    }

    private static void CardMoving(NCardFlyVfx __instance)
    {
        if (active is { image: null, failure: null } shot && shot.AtDecision &&
            FlyingCard.GetValue(__instance) is NCard card && shot.Matches(card))
            shot.flyingCard = card;
    }

    private static bool Showing(NCard? card) => GodotObject.IsInstanceValid(card) && card!.IsVisibleInTree() &&
        card.Body.Scale.X >= .35f && card.Modulate.A > .5f &&
        card.GetViewportRect().HasPoint(card.GetGlobalRect().GetCenter());

    private void PrepareFrame()
    {
        if (image != null || failure != null || restore != null || NCombatRoom.Instance == null ||
            Engine.GetProcessFrames() < drawAfter || RewardChoices.Visible || NMapScreen.Instance?.IsOpen == true ||
            !GodotObject.IsInstanceValid(damage) || !damage!.IsVisibleInTree() ||
            !damage.GetViewportRect().HasPoint(damage.GetGlobalTransformWithCanvas().Origin) ||
            !(Showing(playedCard) || Showing(flyingCard)))
            return;
        restore = hideOverlays();
    }

    private void ReadFrame()
    {
        if (restore == null) return;
        try { image = tree.Root.GetTexture().GetImage(); }
        catch (Exception error) { failure = error; }
        finally
        {
            restore();
            restore = null;
        }
    }

    internal Image? Take()
    {
        if (failure != null) throw new IOException("Could not read the combat highlight.", failure);
        var frame = image;
        image = null;
        return frame;
    }

    public void Dispose()
    {
        RenderingServer.FramePreDraw -= PrepareFrame;
        RenderingServer.FramePostDraw -= ReadFrame;
        restore?.Invoke();
        image?.Dispose();
        active = null;
    }
}

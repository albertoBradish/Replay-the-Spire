using Godot;

namespace SpireReplay.Media;

/// <summary>Supersample the existing 2D viewport while preserving its logical layout and OS window.</summary>
public sealed class VideoRenderTarget : IDisposable
{
    private readonly Window window;
    private readonly Vector2I windowSize;
    private readonly Vector2 scale;
    private bool overridden;

    public Vector2I OriginalSize { get; }
    public Vector2I SourceSize { get; private set; }

    private VideoRenderTarget(Window window, int targetHeight)
    {
        this.window = window;
        windowSize = window.Size;
        OriginalSize = Pixels(window);
        int targetWidth = targetHeight * 16 / 9;
        double factor = Math.Min((double)targetWidth / OriginalSize.X, (double)targetHeight / OriginalSize.Y);
        if (factor <= 1)
        {
            SourceSize = OriginalSize;
            scale = Vector2.One;
            return;
        }
        SourceSize = new Vector2I(Math.Max(2, (int)Math.Round(OriginalSize.X * factor / 2) * 2),
            Math.Max(2, (int)Math.Round(OriginalSize.Y * factor / 2) * 2));
        scale = new Vector2((float)SourceSize.X / OriginalSize.X, (float)SourceSize.Y / OriginalSize.Y);
    }

    public static async Task<VideoRenderTarget> EnsureAsync(Window window, int targetHeight, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (targetHeight is not (720 or 1080)) throw new ArgumentOutOfRangeException(nameof(targetHeight));
        var target = new VideoRenderTarget(window, targetHeight);
        if (target.OriginalSize == target.SourceSize) return target;
        try
        {
            target.overridden = true;
            target.Apply();
            RenderingServer.FramePreDraw += target.Apply;
            await NextDraw(window, ct);
            var actual = Pixels(window);
            if (actual != target.SourceSize)
                throw new InvalidOperationException($"The replay renderer delivered {actual.X}×{actual.Y}; the requested source is {target.SourceSize.X}×{target.SourceSize.Y}.");
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private void Apply()
    {
        var viewport = window.GetViewportRid();
        // A camera can update the scene transform between frames. Reuse its current
        // transform so zooms and shakes retain their position in the larger render.
        var original = window.GetStretchTransform() * window.GlobalCanvasTransform;
        RenderingServer.ViewportSetSize(viewport, SourceSize.X, SourceSize.Y);
        RenderingServer.ViewportSetGlobalCanvasTransform(viewport, Transform2D.Identity.Scaled(scale) * original);
    }

    private static Vector2I Pixels(Window window)
    {
        using var image = window.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new IOException("No rendered replay image is available.");
        return image.GetSize();
    }

    private static async Task NextDraw(Window window, CancellationToken token)
    {
        var tree = window.GetTree();
        bool drawn = false;
        void Drawn() => drawn = true;
        RenderingServer.FramePostDraw += Drawn;
        try
        {
            ulong limit = Time.GetTicksMsec() + 10000;
            while (!drawn)
            {
                token.ThrowIfCancellationRequested();
                if (Time.GetTicksMsec() > limit) throw new TimeoutException("The game window has not rendered a frame.");
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        finally { RenderingServer.FramePostDraw -= Drawn; }
    }

    public void Dispose()
    {
        if (!overridden) return;
        overridden = false;
        RenderingServer.FramePreDraw -= Apply;
        if (!GodotObject.IsInstanceValid(window)) return;
        // Keep the user's current window size if they resized it during the export.
        Vector2I restore = OriginalSize;
        if (window.Size != windowSize)
        {
            var logical = window.GetVisibleRect().Size;
            var stretch = window.GetStretchTransform().Scale;
            restore = new Vector2I(Math.Max(1, Mathf.RoundToInt(logical.X * stretch.X)), Math.Max(1, Mathf.RoundToInt(logical.Y * stretch.Y)));
        }
        var viewport = window.GetViewportRid();
        RenderingServer.ViewportSetSize(viewport, restore.X, restore.Y);
        RenderingServer.ViewportSetGlobalCanvasTransform(viewport, window.GetStretchTransform() * window.GlobalCanvasTransform);
    }
}

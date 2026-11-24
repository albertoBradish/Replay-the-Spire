using Godot;
using SpireReplay.Media;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

internal static class ReplayEncoderDialog
{
    internal static void Open(Window owner, Action ready)
    {
        if (owner.GetNodeOrNull<Window>("EncoderSetup") is { } existing) { existing.GrabFocus(); return; }
        var popup = new Window
        {
            Name = "EncoderSetup", Title = "FFmpeg", Transient = true, Exclusive = true,
            Borderless = true, ProcessMode = Node.ProcessModeEnum.Always
        };
        owner.AddChild(popup);
        var body = Panel(Ink, 16);
        popup.AddChild(body);
        body.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var form = Column(10);
        body.AddChild(form);
        form.AddChild(Text(T("安装视频编码器", "Set up the video encoder"), 21));
        var help = Text(T("MP4 导出使用 FFmpeg。", "MP4 export uses FFmpeg."), 15, true);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        form.AddChild(help);
        var bar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8), Visible = false };
        form.AddChild(bar);
        var download = new Button { Text = T("下载 FFmpeg（约 106 MiB）", "Download FFmpeg (about 106 MiB)"), Name = "DownloadEncoder" };
        var choose = new Button { Text = T("选择已安装的 ffmpeg.exe", "Choose an installed ffmpeg.exe"), Name = "ChooseEncoder" };
        StyleButton(download, true);
        StyleButton(choose);
        form.AddChild(download);
        form.AddChild(choose);
        CancellationTokenSource? install = null;
        bool dismissed = false;

        void Dismiss(bool continueExport = false)
        {
            if (dismissed) return;
            dismissed = true;
            install?.Cancel();
            popup.Hide();
            popup.QueueFree();
            if (continueExport) Callable.From(ready).CallDeferred();
        }
        form.AddChild(Action(T("取消", "Cancel"), () => Dismiss()));
        popup.CloseRequested += () => Dismiss();
        popup.TreeExiting += () => { dismissed = true; install?.Cancel(); };
        download.Pressed += async () =>
        {
            if (install != null) return;
            using var job = new CancellationTokenSource();
            install = job;
            download.Disabled = choose.Disabled = true;
            bar.Visible = true;
            help.Text = T("正在下载 FFmpeg…", "Downloading FFmpeg…");
            var progress = new Progress<double>(value =>
            {
                if (dismissed || !GodotObject.IsInstanceValid(popup) || !double.IsFinite(value)) return;
                int percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
                if ((int)bar.Value == percent) return;
                bar.Value = percent;
                help.Text = percent == 100 ? T("正在准备编码器…", "Preparing the encoder…") :
                    T($"正在下载 FFmpeg · {percent}%", $"Downloading FFmpeg · {percent}%");
            });
            try
            {
                await EncoderInstaller.InstallAsync(progress, job.Token);
                install = null;
                if (!dismissed) Dismiss(continueExport: true);
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested) { }
            catch (Exception e)
            {
                if (!dismissed)
                {
                    help.Text = e.GetBaseException().Message;
                    help.TooltipText = help.Text;
                    bar.Hide();
                }
            }
            finally
            {
                install = null;
                if (!dismissed) download.Disabled = choose.Disabled = false;
            }
        };
        choose.Pressed += () =>
        {
            var picker = new FileDialog
            {
                Title = T("选择 FFmpeg 编码器", "Choose the FFmpeg executable"),
                Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
                Transient = true, Exclusive = true, Filters = ["ffmpeg.exe;FFmpeg", "*;All files"]
            };
            popup.AddChild(picker);
            picker.FileSelected += path =>
            {
                picker.QueueFree();
                try
                {
                    VideoExporter.SetEncoder(path);
                    Dismiss(continueExport: true);
                }
                catch (Exception e) { help.Text = e.GetBaseException().Message; help.TooltipText = help.Text; }
            };
            picker.Canceled += () => picker.QueueFree();
            picker.PopupCentered(new Vector2I(Math.Min(720, owner.Size.X - 20), Math.Min(540, owner.Size.Y - 20)));
        };
        popup.PopupCentered(new Vector2I(Math.Min(440, owner.Size.X - 16), 270));
        download.GrabFocus();
    }
}

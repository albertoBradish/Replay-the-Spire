using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace SpireReplay.Media;

public static class EncoderInstaller
{
    const string Version = "9.0.1";
    const string Asset = "ffmpeg-" + Version + "-essentials_build";
    const string Digest = "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9";
    const long PackageBytes = 111253802;
    static readonly SemaphoreSlim Download = new(1, 1);
    internal static string EncoderPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "ReplayTheSpire", "encoders", Version, "ffmpeg.exe");

    public static async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken token)
    {
        await Download.WaitAsync(token);
        try
        {
            string dir = Path.GetDirectoryName(EncoderPath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(EncoderPath))
            {
                string stage = Path.Combine(dir, ".install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    string archive = Path.Combine(stage, "package.zip");
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                    using var response = await client.GetAsync($"https://github.com/GyanD/codexffmpeg/releases/download/{Version}/{Asset}.zip",
                        HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long bytes = 0;
                    await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    await using (var file = File.Create(archive))
                    {
                        byte[] buffer = new byte[128 * 1024];
                        for (int read; (read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
                        {
                            bytes += read;
                            if (bytes > PackageBytes) throw new InvalidDataException("编码器下载长度不符。");
                            hash.AppendData(buffer.AsSpan(0, read));
                            await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            progress?.Report((double)bytes / PackageBytes);
                        }
                    }
                    if (bytes != PackageBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != Digest)
                        throw new InvalidDataException("编码器校验失败。");
                    using var zip = ZipFile.OpenRead(archive);
                    var executable = zip.GetEntry(Asset + "/bin/ffmpeg.exe") ?? throw new InvalidDataException("未找到编码器。");
                    string extracted = Path.Combine(stage, "ffmpeg.exe");
                    executable.ExtractToFile(extracted);
                    var license = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith(Asset + "/LICENSE", StringComparison.OrdinalIgnoreCase));
                    license?.ExtractToFile(Path.Combine(dir, "FFmpeg-LICENSE.txt"), overwrite: true);
                    token.ThrowIfCancellationRequested();
                    File.Move(extracted, EncoderPath, overwrite: true);
                }
                finally { Directory.Delete(stage, recursive: true); }
            }
            VideoExporter.SetEncoder(EncoderPath);
            return EncoderPath;
        }
        finally { Download.Release(); }
    }
}

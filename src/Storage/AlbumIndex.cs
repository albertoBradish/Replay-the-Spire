using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireReplay;

internal static class AlbumIndex
{
    private sealed record Entry(string Name, string Digest);

    internal static string Key(string runId, int room, string presentation, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"{typeof(Gallery).Module.ModuleVersionId}/{runId}/{room}/{presentation}");
        var source = ReplayStore.RunPath(runId);
        foreach (var file in Directory.EnumerateFiles(source).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (name != "run.json" && !(name.StartsWith(room < 0 ? "room-" : $"room-{room:D4}.", StringComparison.Ordinal) &&
                Path.GetExtension(name) is ".jsonl" or ".checkpoint" or ".mcr")) continue;
            AddFile(hash, file, name, token);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));

    private static void AddFile(IncrementalHash hash, string file, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Append(hash, name);
        using var stream = File.OpenRead(file);
        hash.AppendData(SHA256.HashData(stream));
    }

    private static string Digest(string folder, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var names = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        if (!names.SequenceEqual(["images", "index.html", "index.txt"])) return "";
        AddFile(hash, Path.Combine(folder, "index.html"), "index.html", token);
        AddFile(hash, Path.Combine(folder, "index.txt"), "index.txt", token);
        foreach (string file in Directory.EnumerateFileSystemEntries(Path.Combine(folder, "images")).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(file) != ".jpg" || !File.Exists(file)) return "";
            AddFile(hash, file, Path.GetFileName(file), token);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string IndexPath(string key) => Path.Combine(ReplayStore.LibraryDirectory, ".album-index", key + ".json");

    internal static string? Find(string key, CancellationToken token)
    {
        try
        {
            var link = JsonSerializer.Deserialize<Entry>(File.ReadAllText(IndexPath(key)));
            if (link == null || link.Digest is not { Length: 64 } || string.IsNullOrEmpty(link.Name) || link.Name != Path.GetFileName(link.Name) || !link.Name.StartsWith("Replay-", StringComparison.Ordinal)) return null;
            string folder = Path.Combine(ReplayStore.ExportsDirectory, link.Name);
            return link.Digest == Digest(folder, token) ? Path.Combine(folder, "index.html") : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static void Remember(string key, string folder)
    {
        string path = IndexPath(key);
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string digest = Digest(folder, CancellationToken.None);
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(Path.GetFileName(folder), digest)));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

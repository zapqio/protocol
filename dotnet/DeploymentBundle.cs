using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zapqio.Deployments;

public sealed record DecodedBundle(DeploymentManifest Manifest, byte[] Payload);

public static class DeploymentBundle
{
    public const long MetadataLimit = 4 * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public static bool IsHash(string? hash, int length = 64) => hash is not null && hash.Length == length &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void ValidatePackageName(string name)
    {
        if (name is null || name.Length is < 1 or > 100 || !Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z"))
        {
            throw new InvalidDataException("Invalid module package name.");
        }

        ValidatePath(name);
    }

    public static void ValidatePath(string path)
    {
        if (path.Length is < 1 or > 1024 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
        {
            throw new InvalidDataException("Unsafe archive path.");
        }

        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, @"\A(COM|LPT)[1-9]\z"))
            {
                throw new InvalidDataException("Unsafe archive path.");
            }
        }
    }

    public static Dictionary<string, byte[]> ReadZip(byte[] bytes, long maxBytes, int maxFiles)
    {
        if (maxBytes < 1 || maxFiles < 1 || bytes.LongLength > maxBytes)
        {
            throw new InvalidDataException("Archive exceeds the configured size limit.");
        }

        using var input = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > maxFiles)
        {
            throw new InvalidDataException("Archive exceeds the file count limit.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            ValidatePath(path);
            if (!paths.Add(path))
            {
                throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
            }

            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode != 0 && mode != 0x8000 && mode != 0x4000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                directory && mode == 0x8000 || !directory && mode == 0x4000)
            {
                throw new InvalidDataException("Links and special files are not allowed.");
            }

            if (directory)
            {
                continue;
            }

            if (entry.Length > maxBytes - total)
            {
                throw new InvalidDataException("Archive exceeds the expanded size limit.");
            }

            using var stream = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidDataException("Archive exceeds the expanded size limit.");
                }

                output.Write(buffer, 0, read);
            }
            files.Add(path, output.ToArray());
        }
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                if (files.ContainsKey(string.Join('/', parts.Take(i))))
                {
                    throw new InvalidDataException("A file is also used as a directory.");
                }
            }
        }
        return files;
    }

    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                ValidatePath(file.Key);
                var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(file.Value);
            }
        }
        return output.ToArray();
    }

    public static void ValidateModule(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("##Dll", out var marker))
        {
            throw new InvalidDataException("Module ZIP requires ##Dll.");
        }

        using var markerStream = new MemoryStream(marker, false);
        using var markerReader = new StreamReader(markerStream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string markerText;
        try
        {
            markerText = markerReader.ReadToEnd();
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid ##Dll text encoding.", ex);
        }
        var names = markerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (names.Length == 0 && !files.ContainsKey("##Shared"))
        {
            throw new InvalidDataException("Only a shared module may have an empty ##Dll.");
        }

        foreach (var name in names)
        {
            ValidatePath(name);
            if (name.Contains('/') || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !files.ContainsKey(name))
            {
                throw new InvalidDataException("##Dll references a missing or invalid assembly.");
            }
        }
        if (files.Keys.Any(k => k.Equals("##Hash", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("##Hash is reserved for the runner cache.");
        }
    }

    public static byte[] Create(DeploymentManifest manifest, byte[] payload, long maxBytes, int maxFiles)
    {
        var files = manifest.Action == "Withdraw" ? new Dictionary<string, byte[]>() : ReadZip(payload, maxBytes, maxFiles);
        manifest = manifest with
        {
            PayloadSha256 = Hash(payload),
            PayloadBytes = payload.LongLength,
            Files = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new BundleFile(f.Key, f.Value.LongLength, Hash(f.Value))).ToList()
        };
        var result = Zip(new Dictionary<string, byte[]> { ["bundle.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, DeploymentJson.Options), ["payload.zip"] = payload });
        Read(result, Hash(result), maxBytes, maxFiles);
        return result;
    }

    public static DecodedBundle Read(byte[] bytes, string expectedHash, long maxBytes, int maxFiles)
    {
        if (!IsHash(expectedHash) || Hash(bytes) != expectedHash)
        {
            throw new InvalidDataException("Bundle checksum mismatch.");
        }

        var outer = ReadZip(bytes, checked(maxBytes + MetadataLimit), 2);
        if (outer.Count != 2 || !outer.TryGetValue("bundle.json", out var json) || !outer.TryGetValue("payload.zip", out var payload) || json.LongLength > MetadataLimit)
        {
            throw new InvalidDataException("Bundle requires bundle.json and payload.zip.");
        }

        DeploymentManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeploymentManifest>(json, DeploymentJson.Options) ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid bundle manifest.", ex);
        }
        if (manifest.Files is null || manifest.Files.Any(f => f is null) || manifest.FormatVersion != 1 || manifest.DeploymentId == Guid.Empty || manifest.RepositoryId == Guid.Empty ||
            manifest.SnapshotId == Guid.Empty || manifest.Sequence < 1 || !IsHash(manifest.Commit, 40) ||
            manifest.Action is not "Apply" and not "Withdraw" || manifest.ContentKind is not "DotnetSource" and not "DotnetModuleZip")
        {
            throw new InvalidDataException("Invalid deployment identity or bundle kind.");
        }

        ValidatePackageName(manifest.PackageName);
        if (payload.LongLength != manifest.PayloadBytes || payload.LongLength > maxBytes || Hash(payload) != manifest.PayloadSha256)
        {
            throw new InvalidDataException("Payload checksum or size mismatch.");
        }

        if (manifest.Action == "Withdraw")
        {
            if (payload.Length != 0 || manifest.Files.Count != 0)
            {
                throw new InvalidDataException("Withdraw has no payload.");
            }

            return new(manifest, payload);
        }

        var files = ReadZip(payload, maxBytes, maxFiles);
        var actual = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new BundleFile(f.Key, f.Value.LongLength, Hash(f.Value)));
        if (!actual.SequenceEqual(manifest.Files))
        {
            throw new InvalidDataException("Payload files do not match the manifest.");
        }

        if (manifest.ContentKind == "DotnetModuleZip")
        {
            ValidateModule(files);
        }
        else if (files.Keys.Count(p => !p.Contains('/') && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new InvalidDataException("Source bundle requires exactly one project at the root.");
        }

        return new(manifest, payload);
    }

    public static void Extract(IReadOnlyDictionary<string, byte[]> files, string destination)
    {
        var root = Path.GetFullPath(destination);
        RequireRegularPath(root);
        Directory.CreateDirectory(root);
        foreach (var file in files)
        {
            ValidatePath(file.Key);
            var path = Path.GetFullPath(Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Path leaves the extraction directory.");
            }

            RequireRegularPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(file.Value);
        }
    }

    public static void RequireRegularPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Deployment paths cannot contain links or reparse points.");
            }
        }
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        RequireRegularPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(staging, path, true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}

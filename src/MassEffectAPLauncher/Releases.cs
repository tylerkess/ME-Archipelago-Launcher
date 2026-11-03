// Where each game's files come from: the latest release of that game's own GitHub repository. A launcher built from a
// fork (LauncherRepository, set by tools/publish.py) uses that owner's fork of a game when it has a release, else the
// game's original repository. The launcher downloads
// the release's files into a cache, checks them against the SHA-256 sums the release publishes, and lays them out as the
// payload folder Installer expects (apworld, ASI\<bridge>, <ModFolder>\<DlcName>, optional bink2w64.dll and options.json).
//
// Releases are read as published, so older layouts work unchanged:
//   - a "*payload*.zip" link: only that file is downloaded (already in the payload layout);
//   - otherwise every .apworld / .zip / .asi / .dll asset and an options*.json schema; bundled setup apps are skipped.
// The payload is then assembled by file and folder name, so it does not matter which zip a part came in.
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APSetup;

sealed record ReleaseAsset(string Name, string Url);

sealed class ReleaseInfo
{
    public string Tag, Name, Notes, WebUrl;
    public DateTime Released;
    public List<ReleaseAsset> Assets = [];
    public string Version => Tag.TrimStart('v', 'V');
}

static partial class Releases
{
    const string OriginalLauncher = "Mass-Effect-LE-Archipelago/ME-Archipelago-Launcher";

    // The repository this launcher was built from (assembly metadata LauncherRepository, see the csproj).
    public static readonly string LauncherProject =
        typeof(Releases).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LauncherRepository" && !string.IsNullOrWhiteSpace(a.Value))?.Value ?? OriginalLauncher;

    // Each game's repository; ResolveProjects replaces an entry with the launcher owner's fork when that has a release.
    public static readonly Dictionary<string, string> Projects = new()
    {
        ["ME1"] = "Mass-Effect-LE-Archipelago/ME1-APWorld",
        ["ME2"] = "Mass-Effect-LE-Archipelago/ME2-APWorld",
        ["ME3"] = "Mass-Effect-LE-Archipelago/ME3-AP-World",
    };

    static Task _resolved;

    // Points each game at <launcher owner>/<repository name> when that repository has a release (once per run).
    public static Task ResolveProjects() => _resolved ??= ResolveForks();

    static async Task ResolveForks()
    {
        var owner = LauncherProject.Split('/')[0];
        if (owner.Equals(OriginalLauncher.Split('/')[0], StringComparison.OrdinalIgnoreCase)) return;
        foreach (var (id, project) in Projects.ToList())
        {
            var fork = owner + "/" + project.Split('/')[1];
            try { if (await Latest(fork) != null) Projects[id] = fork; }
            catch (Exception) { }   // no fork (404), or GitHub unreachable: the original repository
        }
    }

    public static string ProjectUrl(string project) => "https://github.com/" + project;

    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MassEffectAPSetup", "downloads");

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MassEffectArchipelagoLauncher/" + typeof(Releases).Assembly.GetName().Version);
        return client;
    }

    static string Api(string project) => "https://api.github.com/repos/" + project;

    // The newest published release (null if the project has none yet).
    public static async Task<ReleaseInfo> Latest(string project, CancellationToken cancel = default)
    {
        // The answer is kept for a few minutes, and used when GitHub can't be reached or rate-limits anonymous
        // API calls (60 an hour, 403/429), so reopening the launcher doesn't ask again and an offline launcher still
        // knows the release.
        var cache = Path.Combine(CacheRoot, "releases", Safe(project.Replace('/', '_')) + ".json");
        string json = null;
        if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromMinutes(10)) json = File.ReadAllText(cache);
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Api(project) + "/releases?per_page=5");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request, cancel);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"{ProjectUrl(project)} was not found (it may be private).");
                if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (File.Exists(cache)) json = File.ReadAllText(cache);
                    else throw new InvalidOperationException("GitHub is rate-limiting release checks; try again in a few minutes.");
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    json = await response.Content.ReadAsStringAsync(cancel);
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    File.WriteAllText(cache, json);
                }
            }
            catch (HttpRequestException) when (File.Exists(cache)) { json = File.ReadAllText(cache); }
            catch (TaskCanceledException) when (File.Exists(cache) && !cancel.IsCancellationRequested) { json = File.ReadAllText(cache); }
        }
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var info = new ReleaseInfo
            {
                Tag = r.GetProperty("tag_name").GetString(),
                Name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                Notes = r.TryGetProperty("body", out var d) ? d.GetString() ?? "" : "",
                Released = r.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTime() : DateTime.MinValue,
                WebUrl = r.TryGetProperty("html_url", out var html) ? html.GetString() : ProjectUrl(project) + "/releases",
            };
            foreach (var asset in r.GetProperty("assets").EnumerateArray())
                info.Assets.Add(new ReleaseAsset(asset.GetProperty("name").GetString(), asset.GetProperty("browser_download_url").GetString()));
            return info;
        }
        return null;
    }

    // The files to download for a game: a payload zip if the release has one, otherwise the game's parts.
    public static List<ReleaseAsset> PayloadAssets(ReleaseInfo release)
    {
        var payload = release.Assets.Where(a => a.Name.Contains("payload", StringComparison.OrdinalIgnoreCase)
                                                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (payload.Count > 0) return payload;
        string[] wanted = [".apworld", ".zip", ".asi", ".dll"];
        bool Schema(ReleaseAsset a) => a.Name.StartsWith("options", StringComparison.OrdinalIgnoreCase)
                                       && a.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return release.Assets.Where(a => (wanted.Any(e => a.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || Schema(a))
                                         && !a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // SHA-256 sums the release publishes: a SHA256SUMS link ("<hash>  <name>" lines) and "SHA-256 of <name>: <hash>" in the notes.
    public static async Task<Dictionary<string, string>> Sums(ReleaseInfo release, CancellationToken cancel = default)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in release.Assets.Where(a => a.Name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase)))
            foreach (var line in (await Http.GetStringAsync(file.Url, cancel)).Split('\n'))
                if (SumLine().Match(line.Trim()) is { Success: true } m) sums[m.Groups["name"].Value.TrimStart('*')] = m.Groups["hash"].Value.ToUpperInvariant();
        foreach (Match m in NotesSum().Matches(release.Notes))
            sums.TryAdd(m.Groups["name"].Value, m.Groups["hash"].Value.ToUpperInvariant());
        return sums;
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+(?<name>.+)$")]
    private static partial Regex SumLine();

    [GeneratedRegex(@"SHA-?256 of `?(?<name>[^\s`:]+)`?:\s*`?(?<hash>[0-9a-fA-F]{64})")]
    private static partial Regex NotesSum();

    public static string CacheDir(string id, ReleaseInfo release) => Path.Combine(CacheRoot, id, Safe(release.Tag));
    public static string PayloadDir(string id, ReleaseInfo release) => Path.Combine(CacheDir(id, release), "payload");

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // The newest payload already downloaded for a game (by folder time), or null.
    public static (string Tag, string Dir)? Cached(string id)
    {
        var root = Path.Combine(CacheRoot, id);
        if (!Directory.Exists(root)) return null;
        var best = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "payload", ".release")) && GameProfile.All.First(p => p.Id == id) is var profile && Complete(profile, Path.Combine(d, "payload")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "payload", ".release")))
            .FirstOrDefault();
        return best == null ? null : (File.ReadAllText(Path.Combine(best, "payload", ".release")).Trim(), Path.Combine(best, "payload"));
    }

    // Downloads the release's files (verified where the release publishes sums) and builds the payload folder.
    // progress: (fraction 0..1, what is happening). Returns the payload folder.
    public static async Task<string> Fetch(GameProfile profile, ReleaseInfo release, IProgress<(double, string)> progress, Action<string> log,
                                           CancellationToken cancel = default)
    {
        var payload = PayloadDir(profile.Id, release);
        if (File.Exists(Path.Combine(payload, ".release")) && Complete(profile, payload))
        {
            log($"{profile.Title} {release.Tag} is already downloaded.");
            return payload;
        }
        var assets = PayloadAssets(release);
        if (assets.Count == 0) throw new InvalidOperationException($"Release {release.Tag} has no files the launcher can install.");
        var sums = await Sums(release, cancel);
        var downloads = Path.Combine(CacheDir(profile.Id, release), "downloads");
        if (Directory.Exists(downloads)) Directory.Delete(downloads, true);   // only this attempt's files go into the payload
        Directory.CreateDirectory(downloads);

        // sizes first, so one progress bar covers every file
        var sizes = new long[assets.Count];
        for (int i = 0; i < assets.Count; i++)
        {
            using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, assets[i].Url), cancel);
            sizes[i] = head.IsSuccessStatusCode ? head.Content.Headers.ContentLength ?? 0 : 0;
        }
        long total = Math.Max(1, sizes.Sum()), done = 0;
        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            var target = Path.Combine(downloads, Safe(asset.Name));
            log($"Downloading {asset.Name}" + (sizes[i] > 0 ? $" ({sizes[i] / 1048576.0:0.0} MB)" : "") + "...");
            using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var file = File.Create(target + ".part");
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress.Report((Math.Min(1.0, (double)done / total) * 0.9, $"Downloading {asset.Name}"));
                }
            }
            File.Move(target + ".part", target, true);
            if (sums.TryGetValue(asset.Name, out var expected))
            {
                var actual = Installer.Hash(target);
                if (actual != expected)
                {
                    File.Delete(target);
                    throw new InvalidOperationException($"{asset.Name} failed its SHA-256 check (expected {expected[..12]}..., got {actual[..12]}...). Try again.");
                }
                log($"  SHA-256 verified.");
            }
            else log($"  (the release publishes no SHA-256 for {asset.Name})");
        }

        progress.Report((0.92, "Unpacking"));
        log("Unpacking...");
        var staging = Path.Combine(CacheDir(profile.Id, release), "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        foreach (var file in assets.Select(a => Path.Combine(downloads, Safe(a.Name))))
        {
            var into = Path.Combine(staging, Path.GetFileNameWithoutExtension(file));
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(file, into);
            else { Directory.CreateDirectory(into); File.Copy(file, Path.Combine(into, Path.GetFileName(file))); }
        }
        if (Directory.Exists(payload)) Directory.Delete(payload, true);
        Assemble(profile, staging, payload);
        File.WriteAllText(Path.Combine(payload, ".release"), release.Tag);
        Directory.Delete(staging, true);
        progress.Report((1.0, "Ready"));
        log($"{profile.Title} {release.Tag} is ready to install.");
        return payload;
    }

    // The parts Installer needs are all there (a payload folder can be damaged after it was built).
    public static bool Complete(GameProfile profile, string payload) =>
        File.Exists(Path.Combine(payload, profile.Apworld))
        && profile.AsiFiles.All(a => File.Exists(Path.Combine(payload, "ASI", a)))
        && Directory.Exists(Path.Combine(payload, profile.ModFolder, profile.DlcName));

    // Builds the payload layout from unpacked release files, finding each part by name.
    public static void Assemble(GameProfile profile, string staging, string payload)
    {
        var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        string FindFile(string name) => files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        string Need(string name) => FindFile(name) ?? throw new InvalidOperationException($"The release has no {name}.");

        Directory.CreateDirectory(Path.Combine(payload, "ASI"));
        File.Copy(Need(profile.Apworld), Path.Combine(payload, profile.Apworld));
        foreach (var asi in profile.AsiFiles) File.Copy(Need(asi), Path.Combine(payload, "ASI", asi));
        if (FindFile("bink2w64.dll") is { } loader) File.Copy(loader, Path.Combine(payload, "bink2w64.dll"));
        if ((FindFile("options.json") ?? FindFile(profile.OptionsResource)) is { } options) File.Copy(options, Path.Combine(payload, "options.json"));

        // the mod: the folder holding DLC_MOD_<...> (with its moddesc.ini and, for ME1, BASEGAME) becomes <ModFolder>
        var dlc = Directory.GetDirectories(staging, profile.DlcName, SearchOption.AllDirectories)
                      .OrderByDescending(d => File.Exists(Path.Combine(Path.GetDirectoryName(d), "moddesc.ini")))
                      .FirstOrDefault() ?? throw new InvalidOperationException($"The release has no {profile.DlcName} folder.");
        CopyTree(Path.GetDirectoryName(dlc), Path.Combine(payload, profile.ModFolder));
    }

    static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    // Plain text for the notes panel: markdown links become their text, emphasis marks go.
    public static string PlainNotes(string markdown) =>
        Emphasis().Replace(MdLink().Replace(markdown ?? "", "${text}"), "");

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex MdLink();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Emphasis();

    // "0.2.10" > "0.2.9"; non-numeric parts compare as text.
    public static int CompareVersions(string a, string b)
    {
        var x = (a ?? "").TrimStart('v', 'V').Split('.', '-', '+');
        var y = (b ?? "").TrimStart('v', 'V').Split('.', '-', '+');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : "0";
            var q = i < y.Length ? y[i] : "0";
            int c = int.TryParse(p, out var pi) && int.TryParse(q, out var qi) ? pi.CompareTo(qi) : string.CompareOrdinal(p, q);
            if (c != 0) return c;
        }
        return 0;
    }
}

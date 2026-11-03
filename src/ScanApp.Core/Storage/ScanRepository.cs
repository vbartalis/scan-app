using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScanApp.Core.Combine;
using ScanApp.Core.Model;

namespace ScanApp.Core.Storage;

/// <summary>
/// A scan folder on disk: <c>scan.json</c>, <c>passes/pass-NNN.bin</c> + <c>.json</c>, <c>results/</c>.
/// A pass exists only once its <c>.json</c> exists; until then its files carry a <c>.tmp</c> suffix.
/// </summary>
public sealed partial class ScanRepository
{
    public const int FormatVersion = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<PassMetadata> _passes;
    private readonly List<CombineResult> _results;
    // A combine saves on the thread pool while the UI reads, opens and deletes results. _resultsLock guards the list
    // only; _saveLock keeps two saves from taking the same id without blocking readers during the write.
    private readonly Lock _resultsLock = new();
    private readonly Lock _saveLock = new();

    private ScanRepository(string directory, ScanInfo info, List<PassMetadata> passes, List<CombineResult> results)
    {
        _results = results;
        Directory = directory;
        Info = info;
        _passes = passes;
    }

    public string Directory { get; }
    public ScanInfo Info { get; }
    public IReadOnlyList<PassMetadata> Passes => _passes;
    /// <summary>A snapshot of the committed results, by id.</summary>
    public IReadOnlyList<CombineResult> Results
    {
        get { lock (_resultsLock) return [.. _results]; }
    }

    internal string PassesDirectory => Path.Combine(Directory, "passes");
    internal string ResultsDirectory => Path.Combine(Directory, "results");

    public static ScanRepository Create(string parentDirectory, string name)
    {
        ValidateName(name);
        string directory = Path.Combine(parentDirectory, name);
        if (System.IO.Directory.Exists(directory) || File.Exists(directory))
            throw new IOException($"A scan named \"{name}\" already exists in {parentDirectory}.");

        System.IO.Directory.CreateDirectory(Path.Combine(directory, "passes"));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "results"));
        var info = new ScanInfo(FormatVersion, name, DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(directory, "scan.json"), JsonSerializer.Serialize(info, Json));
        return new ScanRepository(directory, info, [], []);
    }

    public static ScanRepository Open(string scanDirectory)
    {
        string scanJson = Path.Combine(scanDirectory, "scan.json");
        if (!File.Exists(scanJson))
            throw new InvalidDataException($"{scanDirectory} is not a scan: scan.json is missing.");
        var info = Deserialize<ScanInfo>(scanJson);

        var passes = new List<PassMetadata>();
        string passesDir = Path.Combine(scanDirectory, "passes");
        if (System.IO.Directory.Exists(passesDir))
            foreach (string file in System.IO.Directory.EnumerateFiles(passesDir, "pass-*.json"))
                if (CommittedJsonName().IsMatch(Path.GetFileName(file)))
                    passes.Add(Deserialize<PassMetadata>(file));
        passes.Sort((a, b) => a.Id.CompareTo(b.Id));

        var results = new List<CombineResult>();
        string resultsDir = Path.Combine(scanDirectory, "results");
        if (System.IO.Directory.Exists(resultsDir))
            foreach (string folder in System.IO.Directory.EnumerateDirectories(resultsDir, "result-*"))
                if (CommittedResultName().IsMatch(Path.GetFileName(folder))
                    && !File.Exists(folder + ".deleted") && File.Exists(Path.Combine(folder, "result.json")))
                    results.Add(Deserialize<CombineResult>(Path.Combine(folder, "result.json")));
        results.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new ScanRepository(scanDirectory, info, passes, results);
    }

    public PendingPass BeginPass(PassDirection direction, int pointsPerProfile, bool hasReflectance,
                                 string sourceDescription, IReadOnlyDictionary<string, string> scannerSettings)
    {
        int id = NextPassId();
        var stream = new FileStream(TempBinPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new PassWriter(stream, pointsPerProfile, hasReflectance);
        return new PendingPass(this, id, writer, direction, pointsPerProfile, sourceDescription, scannerSettings, DateTimeOffset.Now);
    }

    public PassReader OpenPass(int passId)
    {
        if (_passes.All(p => p.Id != passId))
            throw new ArgumentException($"Pass {passId} does not exist in this scan.", nameof(passId));
        return new PassReader(new FileStream(BinPath(passId), FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    /// <summary>
    /// Permanently deletes a pass. An empty <c>pass-NNN.deleted</c> marker is written first, so the number is never
    /// handed out again; then the <c>.json</c> goes (the pass stops existing), then the raw <c>.bin</c>.
    /// </summary>
    public void DeletePass(int passId)
    {
        var pass = _passes.FirstOrDefault(p => p.Id == passId)
            ?? throw new ArgumentException($"Pass {passId} does not exist in this scan.", nameof(passId));
        File.WriteAllBytes(DeletedMarkerPath(passId), []);
        File.Delete(EditsPath(passId));
        File.Delete(JsonPath(passId));
        _passes.Remove(pass);
        File.Delete(BinPath(passId));
    }

    /// <summary>The trim edits saved for a pass, or <see cref="PassEdits.None"/> when it has none.</summary>
    public PassEdits LoadEdits(int passId)
    {
        RequirePass(passId);
        string path = EditsPath(passId);
        return File.Exists(path) ? Deserialize<PassEdits>(path) : PassEdits.None;
    }

    /// <summary>Saves a pass's trim edits beside its raw files; saving <see cref="PassEdits.None"/> removes them.</summary>
    public void SaveEdits(int passId, PassEdits edits)
    {
        RequirePass(passId);
        string path = EditsPath(passId);
        if (edits == PassEdits.None)
        {
            File.Delete(path);
            return;
        }
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(edits, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void RequirePass(int passId)
    {
        if (_passes.All(p => p.Id != passId))
            throw new ArgumentException($"Pass {passId} does not exist in this scan.", nameof(passId));
    }

    /// <summary>
    /// Saves a combine result and its merged cloud as <c>results/result-NNN/</c>, written as <c>result-NNN.tmp/</c> and
    /// renamed last. <paramref name="make"/> receives the newly assigned id. Ids are never reused.
    /// </summary>
    public CombineResult SaveResult(Func<int, CombineResult> make, MergedCloud cloud)
    {
        int n = cloud.X.Length;
        if (cloud.Y.Length != n || cloud.Z.Length != n || cloud.PassIndex.Length != n)
            throw new ArgumentException("The cloud's X, Y, Z and PassIndex arrays must have the same length.", nameof(cloud));

        lock (_saveLock)
            return SaveResultLocked(make, cloud);
    }

    private CombineResult SaveResultLocked(Func<int, CombineResult> make, MergedCloud cloud)
    {
        int id = NextResultId();
        var result = make(id);
        if (result.Id != id)
            throw new ArgumentException($"The result must carry the assigned id {id}.", nameof(make));
        if (cloud.PassIndex.Any(i => i >= result.Passes.Count))
            throw new ArgumentException("A PassIndex points past the result's passes.", nameof(cloud));

        System.IO.Directory.CreateDirectory(ResultsDirectory);
        string tmp = ResultFolder(id) + ".tmp";
        if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, recursive: true);
        System.IO.Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "result.json"), JsonSerializer.Serialize(result, Json));
            using (var stream = new FileStream(Path.Combine(tmp, "points.bin"), FileMode.CreateNew, FileAccess.Write))
                ResultFormat.Write(stream, cloud);
            System.IO.Directory.Move(tmp, ResultFolder(id));
        }
        catch
        {
            // A failed save writes nothing: the half-written folder goes too.
            try { System.IO.Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        lock (_resultsLock)
        {
            _results.Add(result);
            _results.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
        return result;
    }

    public MergedCloud OpenResultCloud(int resultId)
    {
        RequireResult(resultId);
        using var stream = new FileStream(Path.Combine(ResultFolder(resultId), "points.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        return ResultFormat.Read(stream);
    }

    /// <summary>
    /// Permanently deletes a result. An empty <c>result-NNN.deleted</c> marker is written first, so the id is never
    /// reused, then <c>result.json</c>, at which point the result no longer exists, then the folder.
    /// </summary>
    public void DeleteResult(int resultId)
    {
        lock (_resultsLock)
        {
            var result = RequireResult(resultId);
            File.WriteAllBytes(ResultFolder(resultId) + ".deleted", []);
            File.Delete(Path.Combine(ResultFolder(resultId), "result.json"));
            _results.Remove(result);
            System.IO.Directory.Delete(ResultFolder(resultId), recursive: true);
        }
    }

    private CombineResult RequireResult(int resultId)
    {
        lock (_resultsLock)
            return _results.FirstOrDefault(r => r.Id == resultId)
                ?? throw new ArgumentException($"Result {resultId} does not exist in this scan.", nameof(resultId));
    }

    internal string ResultFolder(int id) => Path.Combine(ResultsDirectory, $"result-{id:000}");

    /// <summary>One past the highest id of any result folder, committed, left over or deleted.</summary>
    private int NextResultId()
    {
        int max;
        lock (_resultsLock) max = _results.Count == 0 ? 0 : _results.Max(r => r.Id);
        if (System.IO.Directory.Exists(ResultsDirectory))
            foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(ResultsDirectory, "result-*"))
                if (AnyResultName().Match(Path.GetFileName(entry)) is { Success: true } m)
                    max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max + 1;
    }

    /// <summary>Permanently deletes the whole scan folder. Refuses a folder that no longer holds a scan.json.</summary>
    public void DeleteScan()
    {
        Delete(Directory);
        _passes.Clear();
        lock (_resultsLock) _results.Clear();
    }

    /// <summary>
    /// Permanently deletes a scan folder without opening it, so a scan whose passes cannot be read can still go.
    /// Refuses any folder without a scan.json.
    /// </summary>
    public static void Delete(string scanDirectory)
    {
        if (!File.Exists(Path.Combine(scanDirectory, "scan.json")))
            throw new InvalidDataException($"{scanDirectory} holds no scan.json; not deleting it.");
        System.IO.Directory.Delete(scanDirectory, recursive: true);
    }

    internal string DeletedMarkerPath(int id) => Path.Combine(PassesDirectory, $"pass-{id:000}.deleted");
    internal string BinPath(int id) => Path.Combine(PassesDirectory, $"pass-{id:000}.bin");
    internal string JsonPath(int id) => Path.Combine(PassesDirectory, $"pass-{id:000}.json");
    internal string EditsPath(int id) => Path.Combine(PassesDirectory, $"pass-{id:000}.edit.json");
    internal string TempBinPath(int id) => BinPath(id) + ".tmp";
    internal string TempJsonPath(int id) => JsonPath(id) + ".tmp";

    internal void Add(PassMetadata metadata)
    {
        _passes.Add(metadata);
        _passes.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    /// <summary>One past the highest id of any pass file, committed, left over or deleted, so a number is never reused.</summary>
    private int NextPassId()
    {
        int max = _passes.Count == 0 ? 0 : _passes.Max(p => p.Id);
        foreach (string file in System.IO.Directory.EnumerateFiles(PassesDirectory, "pass-*"))
            if (AnyPassFileName().Match(Path.GetFileName(file)) is { Success: true } m)
                max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max + 1;
    }

    private static T Deserialize<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not valid: {e.Message}", e);
        }
    }

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })];

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A scan name cannot be empty.", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"\"{name}\" contains characters a folder name cannot have.", nameof(name));
        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            throw new ArgumentException($"\"{name}\" cannot start with a space or end with a dot or space.", nameof(name));
        string stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"\"{name}\" is a name Windows reserves.", nameof(name));
    }

    [GeneratedRegex(@"^pass-(\d+)\.json$")]
    private static partial Regex CommittedJsonName();

    [GeneratedRegex(@"^result-(\d+)$")]
    private static partial Regex CommittedResultName();

    [GeneratedRegex(@"^result-(\d+)(\.tmp|\.deleted)?$")]
    private static partial Regex AnyResultName();

    [GeneratedRegex(@"^pass-(\d+)\.((bin|json)(\.tmp)?|deleted)$")]
    private static partial Regex AnyPassFileName();
}

/// <summary>A pass being recorded. Its files stay <c>.tmp</c> until <see cref="Commit"/>.</summary>
public sealed class PendingPass : IDisposable
{
    private readonly ScanRepository _repository;
    private readonly PassDirection _direction;
    private readonly int _pointsPerProfile;
    private readonly string _sourceDescription;
    private readonly IReadOnlyDictionary<string, string> _scannerSettings;
    private readonly DateTimeOffset _recordedAt;
    private bool _finished;

    internal PendingPass(ScanRepository repository, int id, PassWriter writer, PassDirection direction, int pointsPerProfile,
                         string sourceDescription, IReadOnlyDictionary<string, string> scannerSettings, DateTimeOffset recordedAt)
    {
        _repository = repository;
        Id = id;
        Writer = writer;
        _direction = direction;
        _pointsPerProfile = pointsPerProfile;
        _sourceDescription = sourceDescription;
        _scannerSettings = scannerSettings;
        _recordedAt = recordedAt;
    }

    public int Id { get; }
    public PassWriter Writer { get; }

    /// <summary>Closes the raw file, then renames <c>.bin</c> first and <c>.json</c> last, which makes the pass exist.</summary>
    public PassMetadata Commit(PassStatus status, string notes = "")
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        Writer.Dispose();
        var metadata = new PassMetadata(Id, _direction, _recordedAt, Writer.ProfilesWritten, _pointsPerProfile, status,
                                        _sourceDescription, new Dictionary<string, string>(_scannerSettings), notes);
        File.WriteAllText(_repository.TempJsonPath(Id), JsonSerializer.Serialize(metadata, ScanRepository.Json));
        File.Move(_repository.TempBinPath(Id), _repository.BinPath(Id));
        File.Move(_repository.TempJsonPath(Id), _repository.JsonPath(Id));
        _repository.Add(metadata);
        return metadata;
    }

    public void Discard()
    {
        if (_finished) return;
        _finished = true;
        Writer.Dispose();
        File.Delete(_repository.TempBinPath(Id));
        File.Delete(_repository.TempJsonPath(Id));
    }

    public void Dispose() => Discard();
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderProcessingSystem.Storage;

/// <summary>
/// The sole JSON collection reader/writer. Per-file semaphores serialize reads and
/// mutations within this process, while temp-file replacement prevents partial JSON files.
/// </summary>
public sealed class JsonFileStore
{
    private readonly string _dataDirectory;
    private static readonly object LocksGate = new();
    private static readonly Dictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFileStore()
        : this(Path.Combine(AppContext.BaseDirectory, "Data")) { }

    public JsonFileStore(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
    }

    public string DataDirectory => _dataDirectory;

    private string GetPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("Only a file name (not a path) is accepted.", nameof(fileName));
        return Path.Combine(_dataDirectory, fileName);
    }

    private SemaphoreSlim GetLock(string fileName)
    {
        string key = GetPath(fileName);
        lock (LocksGate)
        {
            if (!FileLocks.TryGetValue(key, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                FileLocks.Add(key, semaphore);
            }
            return semaphore;
        }
    }

    public async Task<List<T>> ReadAsync<T>(string fileName, CancellationToken ct)
    {
        var semaphore = GetLock(fileName);
        await semaphore.WaitAsync(ct);
        try { return await ReadUnlockedAsync<T>(fileName, ct); }
        finally { semaphore.Release(); }
    }

    public async Task WriteAsync<T>(string fileName, List<T> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        var semaphore = GetLock(fileName);
        await semaphore.WaitAsync(ct);
        try { await WriteUnlockedAsync(fileName, records, ct); }
        finally { semaphore.Release(); }
    }

    /// <summary>Read-modify-write under one lock, so concurrent collection updates are not lost.</summary>
    public async Task UpdateAsync<T>(string fileName, Func<List<T>, List<T>> update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var semaphore = GetLock(fileName);
        await semaphore.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var records = await ReadUnlockedAsync<T>(fileName, ct);
            var updated = update(records) ?? throw new InvalidOperationException("An update callback returned null.");
            await WriteUnlockedAsync(fileName, updated, ct);
        }
        finally { semaphore.Release(); }
    }

    // Synchronous variants exist only for the Phase 1 synchronous comparison mode.
    public List<T> Read<T>(string fileName)
    {
        var semaphore = GetLock(fileName);
        semaphore.Wait();
        try { return ReadUnlocked<T>(fileName); }
        finally { semaphore.Release(); }
    }

    public void Write<T>(string fileName, List<T> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var semaphore = GetLock(fileName);
        semaphore.Wait();
        try { WriteUnlocked(fileName, records); }
        finally { semaphore.Release(); }
    }

    public void Update<T>(string fileName, Func<List<T>, List<T>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var semaphore = GetLock(fileName);
        semaphore.Wait();
        try
        {
            var current = ReadUnlocked<T>(fileName);
            WriteUnlocked(fileName, update(current) ?? throw new InvalidOperationException("An update callback returned null."));
        }
        finally { semaphore.Release(); }
    }

    private async Task<List<T>> ReadUnlockedAsync<T>(string fileName, CancellationToken ct)
    {
        string path = GetPath(fileName);
        if (!File.Exists(path)) return new List<T>();
        string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return Deserialize<T>(json, fileName);
    }

    private List<T> ReadUnlocked<T>(string fileName)
    {
        string path = GetPath(fileName);
        if (!File.Exists(path)) return new List<T>();
        string json = File.ReadAllText(path, Encoding.UTF8);
        return Deserialize<T>(json, fileName);
    }

    private static List<T> Deserialize<T>(string json, string fileName)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>(); }
        catch (JsonException ex) { throw new DataFileCorruptedException(fileName, ex); }
    }

    private async Task WriteUnlockedAsync<T>(string fileName, List<T> records, CancellationToken ct)
    {
        string path = GetPath(fileName);
        string tempPath = Path.Combine(_dataDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(records, JsonOptions);
        try
        {
            await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            ct.ThrowIfCancellationRequested();
            // Same-directory rename/replace: readers see the old complete file or the new complete file.
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void WriteUnlocked<T>(string fileName, List<T> records)
    {
        string path = GetPath(fileName);
        string tempPath = Path.Combine(_dataDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(records, JsonOptions);
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

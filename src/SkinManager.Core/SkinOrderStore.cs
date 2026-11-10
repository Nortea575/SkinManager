using System.Text.Json;

namespace SkinManager.Core;

/// <summary>
/// Stores the user's library order independently from skin packages and their IDs.
/// </summary>
public sealed class SkinOrderStore
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public SkinOrderStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<string> Reconcile(IEnumerable<string> catalogIds)
    {
        ArgumentNullException.ThrowIfNull(catalogIds);
        var actualIds = catalogIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(IdComparer)
            .ToArray();
        var actualById = actualIds.ToDictionary(id => id, IdComparer);
        var persisted = ReadIds();
        var result = new List<string>(actualIds.Length);
        var seen = new HashSet<string>(IdComparer);

        foreach (var id in persisted)
        {
            if (actualById.TryGetValue(id, out var actualId) && seen.Add(actualId))
            {
                result.Add(actualId);
            }
        }

        foreach (var id in actualIds)
        {
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        if (!persisted.SequenceEqual(result, StringComparer.Ordinal))
        {
            WriteIds(result);
        }

        return result;
    }

    public void SaveOrder(IEnumerable<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var ids = orderedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(IdComparer)
            .ToArray();
        WriteIds(ids);
    }

    public bool Move(string id, string targetId, bool insertAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var ids = ReadIds().ToList();
        var sourceIndex = ids.FindIndex(candidate => IdComparer.Equals(candidate, id));
        var targetIndex = ids.FindIndex(candidate => IdComparer.Equals(candidate, targetId));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return false;
        }

        var original = ids.ToArray();
        var moving = ids[sourceIndex];
        ids.RemoveAt(sourceIndex);
        targetIndex = ids.FindIndex(candidate => IdComparer.Equals(candidate, targetId));
        var insertionIndex = targetIndex + (insertAfter ? 1 : 0);
        insertionIndex = Math.Clamp(insertionIndex, 0, ids.Count);
        ids.Insert(insertionIndex, moving);
        if (original.SequenceEqual(ids, IdComparer))
        {
            return false;
        }

        WriteIds(ids);
        return true;
    }

    private string[] ReadIds()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path), JsonOptions);
            return ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(IdComparer).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteIds(IReadOnlyList<string> ids)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("皮肤排序文件路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(ids, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

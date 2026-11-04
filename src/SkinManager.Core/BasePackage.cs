using System.Collections.ObjectModel;

namespace SkinManager.Core;

public sealed class BasePackage
{
    internal BasePackage(
        string id,
        string name,
        string sourceVersion,
        string sourceAsarSha256,
        string entry,
        IReadOnlyList<string> styles,
        IReadOnlyList<string> parts,
        IReadOnlyDictionary<string, byte[]> files)
    {
        Id = id;
        Name = name;
        SourceVersion = sourceVersion;
        SourceAsarSha256 = sourceAsarSha256;
        Entry = entry;
        Styles = Array.AsReadOnly(styles.ToArray());
        Parts = Array.AsReadOnly(parts.ToArray());
        Files = new ReadOnlyDictionary<string, byte[]>(
            files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    public string Id { get; }
    public string Name { get; }
    public string SourceVersion { get; }
    public string SourceAsarSha256 { get; }
    public string Entry { get; }
    public IReadOnlyList<string> Styles { get; }
    public IReadOnlyList<string> Parts { get; }
    public IReadOnlyDictionary<string, byte[]> Files { get; }
    public byte[] EntryBytes => Files[Entry].ToArray();
}

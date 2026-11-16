using System.Text.Json;

namespace IsSeattleFull.Parcels;

public sealed class AerialBoundaryStore(string path)
{
    private readonly object gate = new();
    private AerialBoundaryDocument? document;
    private DateTime modified;
    private long length;

    public (string Revision, JsonElement? Geometry) Read(string id)
    {
        lock (gate)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Aerial boundaries have not been prepared.", path);
            if (document is null || modified != file.LastWriteTimeUtc || length != file.Length)
            {
                var loaded = JsonSerializer.Deserialize<AerialBoundaryDocument>(File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (loaded is null || loaded.Groups is null || string.IsNullOrWhiteSpace(loaded.Revision))
                    throw new InvalidDataException("Aerial boundary export is incomplete.");
                document = loaded;
                modified = file.LastWriteTimeUtc;
                length = file.Length;
            }
            return (document.Revision, document.Groups.TryGetValue(id, out var geometry) ? geometry : null);
        }
    }
}

using System.Text.Json;

namespace MailTrim.Core;

public sealed record ReleaseCandidate(string Tag, Version Version, bool Prerelease);

public static class ReleaseCatalog
{
    public static ReleaseCandidate? Select(string json, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        ReleaseCandidate? best = null;
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                || !entry.TryGetProperty("prerelease", out var pre) || pre.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || (!includePrereleases && pre.GetBoolean())
                || !entry.TryGetProperty("tag_name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var tag = name.GetString()!;
            var number = tag.StartsWith('v') ? tag[1..] : tag;
            if (!Version.TryParse(number, out var version) || version.Build < 0) continue;
            version = new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
            if (best is null || version > best.Version || (version == best.Version && best.Prerelease && !pre.GetBoolean()))
                best = new ReleaseCandidate(tag, version, pre.GetBoolean());
        }
        return best;
    }
}

namespace MailTrim.Core;

public enum DistributionKind { Standalone, Lite }

public static class ReleasePackage
{
    // Keep the original name for standalone packages so older updaters still work.
    public static string Name(Version version, DistributionKind kind) =>
        $"MailTrim-{version.ToString(3)}-win-x64{(kind switch
        {
            DistributionKind.Standalone => "",
            DistributionKind.Lite => "-lite",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        })}.zip";
}

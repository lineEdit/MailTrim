using System.Reflection;
using MailTrim.Core;

namespace MailTrim.App;

internal static class AppDistribution
{
    public static DistributionKind Current { get; } = typeof(App).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(value => value.Key == "MailTrimDistribution" && value.Value == "Lite")
        ? DistributionKind.Lite : DistributionKind.Standalone;
}

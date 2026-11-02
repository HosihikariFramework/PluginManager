using System.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari.Internal.Pack;

/// <summary>The packer's operator-facing resource set.</summary>
internal sealed class PackStrings;

/// <summary>
/// Localized packer text. The lookup reads the current UI culture on every call, so the
/// author's locale decides the language and English is the neutral fallback.
/// </summary>
internal static class PackText
{
    private const string ResourceBaseName = "Hosihikari.Plugin.Pack.Resources.PackStrings";

    private static readonly IStringLocalizer Localizer =
        new ResourceManagerStringLocalizer(
            new ResourceManager(ResourceBaseName, typeof(PackStrings).Assembly),
            typeof(PackStrings).Assembly,
            ResourceBaseName,
            new ResourceNamesCache(),
            NullLogger.Instance);

    internal static string Get(string key, params object[] arguments) => Localizer[key, arguments];
}

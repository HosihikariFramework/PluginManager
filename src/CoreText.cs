using System.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari;

/// <summary>The core's operator-facing resource set.</summary>
internal sealed class CoreStrings;

/// <summary>
/// Localized operator text. The lookup reads the current UI culture on every call, so the
/// server's locale decides the language and English is the neutral fallback.
/// </summary>
internal static class CoreText
{
    private const string ResourceBaseName = "Hosihikari.Resources.CoreStrings";

    private static readonly IStringLocalizer Localizer =
        new ResourceManagerStringLocalizer(
            new ResourceManager(ResourceBaseName, typeof(CoreStrings).Assembly),
            typeof(CoreStrings).Assembly,
            ResourceBaseName,
            new ResourceNamesCache(),
            NullLogger.Instance);

    internal static string Get(string key, params object[] arguments) => Localizer[key, arguments];
}

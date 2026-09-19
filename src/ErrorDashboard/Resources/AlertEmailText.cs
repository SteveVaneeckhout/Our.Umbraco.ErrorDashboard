using System.Globalization;
using System.Resources;
using Umbraco.Cms.Core.Models.Membership;

namespace Our.Umbraco.ErrorDashboard.Resources;

/// <summary>
///     The alert email's wording, in the language of whoever is receiving it.
/// </summary>
/// <remarks>
///     <para>
///         This is the only text in the package that cannot be localized in the browser. Everything a
///         dashboard renders comes from the client dictionary in <c>Client/src/lang</c>; an email has
///         no browser, so the server has to pick a language, and the only sensible one is the
///         recipient's own <see cref="IUser.Language" />.
///     </para>
///     <para>
///         Adding a language is one more <c>AlertEmailResources.&lt;culture&gt;.resx</c> beside the
///         English one - .NET builds it into a satellite assembly and resolves it through the normal
///         fallback chain, so <c>nl-NL</c> finds <c>nl</c>, and anything with no translation at all
///         finds English.
///     </para>
/// </remarks>
internal static class AlertEmailText
{
    private static readonly ResourceManager Resources = new(
        $"{typeof(AlertEmailText).Namespace}.AlertEmailResources",
        typeof(AlertEmailText).Assembly);

    /// <summary>
    ///     Resolves a backoffice user's language to a culture, falling back to the invariant culture
    ///     rather than throwing.
    /// </summary>
    /// <remarks>
    ///     The value comes from the user's profile and is not validated on the way in, so a stale or
    ///     hand-edited row can hold something that is not a culture name at all. An alert is exactly
    ///     the wrong thing to lose over that.
    /// </remarks>
    public static CultureInfo CultureFor(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Looks up a string, falling back to the key so a missing entry is obvious rather than blank.</summary>
    public static string Get(string name, CultureInfo culture)
        => Resources.GetString(name, culture) ?? name;

    /// <summary>Looks up a string and fills in its placeholders, formatting them for the same culture.</summary>
    public static string Get(string name, CultureInfo culture, params object?[] arguments)
        => string.Format(culture, Get(name, culture), arguments);
}

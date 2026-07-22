using Serilog;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NugetTranslation.Translation;

internal static class XmlValidator
{
    private static readonly Regex FenceRegex = new(@"```xml\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    public static XElement? TryParse(string text)
    {
        try { return XElement.Parse(text); }
        catch { return null; }
    }

    public static XElement ParseOrThrow(string text)
    {
        try { return XElement.Parse(text); }
        catch (Exception ex)
        {
            Log.Logger.Debug("无法解析 XML: {Text}", text);
            throw new XmlException("XML 解析失败", ex);
        }
    }

    public static bool NameMatches(XElement element, string expected)
    {
        return (string?)element.Attribute("name") == expected;
    }

    public static string StripFence(string text)
    {
        if (FenceRegex.Match(text) is { Success: true, Groups.Count: > 1 } match)
        {
            text = match.Groups[1].Value;
        }

        return text.Trim();
    }

    public static string ExtractName(string memberXml)
    {
        var match = Regex.Match(memberXml, @"name\s*=\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : "";
    }
}

using System.Xml;

namespace Watchpoint.Model.Documents.Library;

/// <summary>Symbol checks (spec SCM §6.3, §9): no script, event handlers, external references, raster images or foreign objects; ≤ 200 KB.</summary>
public static class SvgSafety
{
    /// <summary>Maximum SVG size.</summary>
    public const int MaxBytes = 200 * 1024;

    private static readonly HashSet<string> _forbiddenElements = new(StringComparer.OrdinalIgnoreCase) { "script", "image", "foreignObject" };

    /// <summary>Checks an SVG file.</summary>
    /// <param name="path">The file.</param>
    /// <returns><see langword="null"/> when safe, else the reason.</returns>
    public static string? Check(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            return "size";
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(path, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (_forbiddenElements.Contains(reader.LocalName))
                {
                    return $"element <{reader.LocalName}>";
                }

                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"event handler {reader.LocalName}";
                    }

                    if (reader.LocalName == "href" && !reader.Value.StartsWith('#'))
                    {
                        return $"external reference {reader.Value}";
                    }
                }

                reader.MoveToElement();
            }
        }
        catch (XmlException ex)
        {
            return $"invalid XML: {ex.Message}";
        }

        return null;
    }
}

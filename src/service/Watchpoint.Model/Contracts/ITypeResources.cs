using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Contracts;

/// <summary>Resource provider for type translations and SVG symbols (spec MOD §4.5).</summary>
public interface ITypeResources
{
    /// <summary>Gets the merged nested i18next JSON translation bundle for the given culture.</summary>
    /// <param name="culture">The culture code (e.g. <c>"en"</c>, <c>"fr"</c>, <c>"ar"</c>).</param>
    /// <param name="etag">The computed ETag header for HTTP caching.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    ReadOnlyMemory<byte> GetTranslationBundle(string culture, out string etag);

    /// <summary>Gets the SVG bytes for the requested symbol and overall state, falling back to <c>default.svg</c>.</summary>
    /// <param name="symbolId">The symbol identifier (e.g. <c>"ac.door"</c>).</param>
    /// <param name="state">The device state.</param>
    /// <returns>The SVG bytes, or <c>model.symbol.not_found</c> if symbol does not exist.</returns>
    Result<ReadOnlyMemory<byte>> GetSymbol(string symbolId, OverallState state);
}

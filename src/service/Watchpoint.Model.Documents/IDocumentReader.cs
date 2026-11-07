using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents;

public interface IDocumentReader
{
    Result<CapabilityDocument> ReadCapability(ReadOnlySpan<byte> utf8, string sourcePath);
    Result<DeviceTypeDocument> ReadDeviceType(ReadOnlySpan<byte> utf8, string sourcePath);
    Result<ManifestDocument>   ReadManifest(ReadOnlySpan<byte> utf8, string sourcePath);
}

using Shouldly;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class NamingTests
{
    [Fact]
    public void STO_U_006_Naming_schema_per_module()
    {
        StorageNaming.Schemas.SchemaFor("Registry").ShouldBe("wp_registry");
        StorageNaming.Schemas.SchemaFor("Identity").ShouldBe("wp_identity");
        StorageNaming.Schemas.TableName("wp_registry", "devices").ShouldBe(("wp_registry", "devices"));
        Should.Throw<ArgumentException>(() => StorageNaming.Schemas.SchemaFor("Reg; DROP"));
    }

    [Fact]
    public void STO_U_007_Naming_table_prefix_fallback()
    {
        var naming = StorageNaming.TablePrefixes;

        naming.UsesSchemas.ShouldBeFalse();
        naming.TableName(naming.SchemaFor("Registry"), "devices").ShouldBe((null, "wp_registry__devices"));
    }
}

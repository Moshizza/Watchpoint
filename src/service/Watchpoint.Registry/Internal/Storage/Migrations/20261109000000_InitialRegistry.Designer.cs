#pragma warning disable IDE0005

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Registry.Internal.Storage.Migrations;

[DbContext(typeof(RegistryPostgresDbContext))]
[Migration("20261109000000_InitialRegistry")]
internal sealed partial class InitialRegistry
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema("wp_registry")
            .HasAnnotation("ProductVersion", "10.0.14")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

#pragma warning restore 612, 618
    }
}

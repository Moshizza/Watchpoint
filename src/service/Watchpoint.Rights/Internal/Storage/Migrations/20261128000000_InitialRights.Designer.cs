#pragma warning disable IDE0005

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Rights.Internal.Storage.Migrations;

[DbContext(typeof(RightsPostgresDbContext))]
[Migration("20261128000000_InitialRights")]
internal sealed partial class InitialRights
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema("wp_rights")
            .HasAnnotation("ProductVersion", "10.0.14")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

#pragma warning restore 612, 618
    }
}

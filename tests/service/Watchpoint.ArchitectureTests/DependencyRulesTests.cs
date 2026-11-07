using Shouldly;
using Xunit;

namespace Watchpoint.ArchitectureTests;

[Trait("layer", "I")]
[Trait("spec", "TST")]
public sealed class DependencyRulesTests
{
    private static IReadOnlyList<ArchAssembly> Solution => SolutionAssemblies.All;

    [Fact]
    public void TST_I_008_Solution_assemblies_are_discovered() =>
        Solution.Select(a => a.Name).ShouldContain(ArchitectureRules.Kernel);

    [Fact]
    public void TST_I_008_ARCH_1_Kernel_depends_on_no_other_Watchpoint_assembly() => ArchitectureRules.Arch1(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_2_No_assembly_depends_on_Host() => ArchitectureRules.Arch2(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_3_Modules_use_other_modules_only_through_contracts() => ArchitectureRules.Arch3(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_4_Internal_namespaces_have_no_public_types() => ArchitectureRules.Arch4(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_5_Only_storage_references_EF_Core_providers() => ArchitectureRules.Arch5(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_6_Integration_Abstractions_depends_only_on_Kernel_and_BCL() => ArchitectureRules.Arch6(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_7_Connectors_do_not_reference_service_modules() => ArchitectureRules.Arch7(Solution).ShouldBeEmpty();

    [Fact]
    public void TST_I_008_ARCH_8_No_controllers() => ArchitectureRules.Arch8(Solution).ShouldBeEmpty();
}

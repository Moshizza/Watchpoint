using Shouldly;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Kernel.Tests.Paging;

public sealed class PagingTests
{
    private static readonly HashSet<string> s_allowed = new(StringComparer.Ordinal) { "name", "createdAt", "id" };
    private static readonly SortSpec s_default = new([new SortField("name", false)]);

    [Fact]
    public void K_U_029_PageRequest_Create_limits()
    {
        var byDefault = PageRequest.Create(null, null);
        byDefault.Value.ShouldBe(new PageRequest(100, null));

        PageRequest.Create(1, "abc").Value.ShouldBe(new PageRequest(1, "abc"));
        PageRequest.Create(1000, "").Value.ShouldBe(new PageRequest(1000, null));

        foreach (var limit in new[] { 0, -1, 1001 })
        {
            var result = PageRequest.Create(limit, null);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(CommonErrorCodes.ValueOutOfRange);
            result.Error.Kind.ShouldBe(ErrorKind.Validation);
            result.Error.Args["min"].ShouldBe(1L);
            result.Error.Args["max"].ShouldBe(1000L);
        }
    }

    [Fact]
    public void K_U_030_SortSpec_Parse()
    {
        var parsed = SortSpec.Parse("name,-createdAt", s_allowed, s_default);
        parsed.Value.Fields.ShouldBe([new SortField("name", false), new SortField("createdAt", true)]);

        SortSpec.Parse(" -id , name ", s_allowed, s_default).Value.Fields
            .ShouldBe([new SortField("id", true), new SortField("name", false)]);

        var unknown = SortSpec.Parse("name,-size", s_allowed, s_default);
        unknown.IsSuccess.ShouldBeFalse();
        unknown.Error.Code.ShouldBe(CommonErrorCodes.SortInvalidField);
        unknown.Error.Kind.ShouldBe(ErrorKind.Validation);
        unknown.Error.Args["field"].ShouldBe("-size");
        unknown.Error.Args["allowed"].ShouldBe(new List<string> { "createdAt", "id", "name" });

        SortSpec.Parse("name,,id", s_allowed, s_default).Error.Code.ShouldBe(CommonErrorCodes.SortInvalidField);
        SortSpec.Parse("name,-name", s_allowed, s_default).Error.Code.ShouldBe(CommonErrorCodes.SortInvalidField);
        SortSpec.Parse("Name", s_allowed, s_default).Error.Code.ShouldBe(CommonErrorCodes.SortInvalidField);

        SortSpec.Parse(null, s_allowed, s_default).Value.ShouldBeSameAs(s_default);
        SortSpec.Parse("  ", s_allowed, s_default).Value.ShouldBeSameAs(s_default);
    }
}

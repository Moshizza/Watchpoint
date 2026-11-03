using System.Text;
using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Tests.Json;
using Watchpoint.Kernel.Text;
using Xunit;

namespace Watchpoint.Kernel.Tests.Text;

public sealed class TextTests
{
    [Fact]
    public void K_U_045_Message_Of_arguments()
    {
        var device = DeviceId.New();
        var message = Message.Of("events.door.forced.title", ("deviceId", device), ("count", 3), ("name", "Lobby"));

        message.Key.ShouldBe("events.door.forced.title");
        message.Args.Count.ShouldBe(3);
        message.Args["deviceId"].ShouldBe(device);
        message.Args["count"].ShouldBe(3);

        Message.Of("events.door.forced.title").Args.ShouldBeEmpty();

        Should.Throw<ArgumentException>(() => Message.Of("events.x", ("a", new object())));
        Should.Throw<ArgumentException>(() => Message.Of("events.x", ("Bad", 1)));
        Should.Throw<ArgumentException>(() => Message.Of("Events.Door"));
        Should.Throw<ArgumentException>(() => Message.Of("door"));
        Should.Throw<ArgumentException>(() => message with { Args = new Dictionary<string, object?> { ["a"] = new List<int>() } });
    }

    [Fact]
    public void K_U_046_LocalizedName_Resolve_fallback()
    {
        var name = new LocalizedName("Main entrance", new Dictionary<string, string> { ["fr"] = "Entrée principale" });

        name.Resolve("fr-CA").ShouldBe("Entrée principale");
        name.Resolve("fr").ShouldBe("Entrée principale");
        name.Resolve("de").ShouldBe("Main entrance");
        name.Resolve("en").ShouldBe("Main entrance");
        LocalizedName.Of("Lobby").Resolve("ar").ShouldBe("Lobby");

        var options = WatchpointJson.Create(JsonProfile.Document, TestJsonContext.Default);
        var json = JsonSerializer.Serialize(new NameHolder(name), options);
        json.ShouldBe("""{"name":{"default":"Main entrance","translations":{"fr":"Entrée principale"}}}""".Replace("é", "\\u00E9", StringComparison.Ordinal));
        JsonSerializer.Deserialize<NameHolder>(json, options)!.Name.Resolve("fr").ShouldBe("Entrée principale");
    }

    [Fact]
    public void K_U_047_LocalizedName_validation()
    {
        var name = new LocalizedName("Main entrance", new Dictionary<string, string> { ["de"] = "Haupteingang", ["fr"] = "Entrée" });

        var errors = name.Validate("");

        errors.Count.ShouldBe(1);
        errors[0].Path.ShouldBe("/translations/de");
        errors[0].Code.ShouldBe(CommonErrorCodes.ValueNotAllowed);
        errors[0].Args["allowed"].ShouldBe(SupportedCultures.All);

        new LocalizedName(" x", new Dictionary<string, string> { ["fr"] = "" }).Validate("/name")
            .Select(e => (e.Path, e.Code.Value))
            .ShouldBe([("/name/default", "common.value.invalid_format"), ("/name/translations/fr", "common.value.required")]);

        new LocalizedName("Lobby", new Dictionary<string, string> { ["ar"] = "المدخل" }).Validate("/name").ShouldBeEmpty();
        SupportedCultures.All.ShouldBe(["en", "fr", "ar"]);
        SupportedCultures.Default.ShouldBe("en");
        SupportedCultures.IsRightToLeft("ar-MA").ShouldBeTrue();
        SupportedCultures.IsRightToLeft("fr").ShouldBeFalse();
    }

    [Fact]
    public void K_U_048_NameRules_valid_names()
    {
        foreach (var name in new[] { "Main entrance", "المدخل", new string('a', 200), "A", "Porte n°3 – entrée" })
        {
            NameRules.Validate(name, "/name").ShouldBeEmpty(name);
        }
    }

    [Fact]
    public void K_U_049_NameRules_invalid_names()
    {
        Codes("").ShouldBe(["common.value.required"]);
        Codes("  ").ShouldBe(["common.value.required"]);
        Codes(null).ShouldBe(["common.value.required"]);
        Codes(new string('a', 201)).ShouldBe(["common.value.too_long"]);
        Codes("a\u0007b").ShouldBe(["common.value.invalid_format"]);
        Codes(" lead").ShouldBe(["common.value.invalid_format"]);
        Codes("trail\t").ShouldBe(["common.value.invalid_format", "common.value.invalid_format"]);

        var tooLong = NameRules.Validate(new string('a', 201), "/name").Single();
        tooLong.Path.ShouldBe("/name");
        tooLong.Args["max"].ShouldBe(200);

        static string[] Codes(string? value) => [.. NameRules.Validate(value, "/name").Select(e => e.Code.Value)];
    }

    [Fact]
    public void K_U_050_NameRules_NFC_normalisation_and_comparison()
    {
        var decomposed = "é";
        var composed = "É";

        NameRules.Normalize(decomposed).ShouldBe("é");
        NameRules.Normalize(decomposed).IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
        NameRules.AreEquivalent(decomposed, composed).ShouldBeTrue();
        NameRules.AreEquivalent("Main Entrance", "main entrance").ShouldBeTrue();
        NameRules.AreEquivalent("Lobby", "Lobby 2").ShouldBeFalse();
        NameRules.Validate(new string('e', 100) + string.Concat(Enumerable.Repeat(decomposed, 100)), "/name").ShouldBeEmpty();
    }
}

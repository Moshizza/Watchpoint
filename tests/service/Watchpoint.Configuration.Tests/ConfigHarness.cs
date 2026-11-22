using System.Text.Json;
using Watchpoint.Configuration.Adapters;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Validation;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Testing.Ids;

namespace Watchpoint.Configuration.Tests;

/// <summary>The configuration module on fakes, with the object types of the existing modules over an in-memory document source.</summary>
internal sealed class ConfigHarness
{
    public ConfigHarness(ConfigurationSettings? settings = null, bool registerModuleTypes = true, params IConfigurableObjectType[] extra)
    {
        Settings = settings ?? new ConfigurationSettings();
        Types = new ObjectTypeRegistry();
        if (registerModuleTypes)
        {
            foreach (var type in ModuleObjectTypes.Create(Source))
            {
                Types.Register(type);
            }
        }

        foreach (var type in extra)
        {
            Types.Register(type);
        }

        Repository = new InMemoryDraftRepository();
        var secrets = new DraftSecrets(Vault);
        Validator = new DraftValidator(Types, Licence);
        Drafts = new DraftService(Repository, Types, Authorizer, secrets, Validator, Settings, TimeProvider.System);
        Approval = new TestApproval(Settings);
        Quick = new QuickEdit(Drafts, Types, Authorizer, Approval, Publisher);
    }

    public static UserId Alice { get; } = TestIds.User(1);

    public static UserId Bob { get; } = TestIds.User(2);

    public ConfigurationSettings Settings { get; }

    public ObjectTypeRegistry Types { get; }

    public FakeSource Source { get; } = new();

    public FakeAuthorizer Authorizer { get; } = new();

    public FakeVault Vault { get; } = new();

    public FakePublisher Publisher { get; } = new();

    public FakeLicence Licence { get; } = new();

    public InMemoryDraftRepository Repository { get; }

    public DraftValidator Validator { get; }

    public DraftService Drafts { get; }

    public TestApproval Approval { get; }

    public QuickEdit Quick { get; }

    public static Guid Id(long n) => TestIds.Uuid(n);

    public static ConfigObjectRef Ref(string type, long n) => new(type, Id(n));

    public static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public async Task<Draft> NewDraftAsync(UserId? owner = null)
    {
        var result = await Drafts.CreateAsync(LocalizedName.Of("Test draft"), owner ?? Alice, CancellationToken.None);
        return result.Value;
    }

    /// <summary>Sets an entry and returns the resulting draft; throws when it fails.</summary>
    public async Task<Draft> SetAsync(Draft draft, string type, long n, EntryOperation op, string? json, long? baseVersion = null, UserId? actor = null)
    {
        var request = new EntryRequest(Ref(type, n), op, baseVersion, json is null ? null : Doc(json));
        var result = await Drafts.SetEntryAsync(draft.Id, actor ?? Alice, draft.Version, request, CancellationToken.None);
        return result.Value.Draft;
    }

    internal sealed class TestApproval(ConfigurationSettings settings) : IApprovalPolicy
    {
        public bool RequiresApproval(IReadOnlyCollection<string> objectTypes) => objectTypes.Any(t => settings.ApprovalRequiredFor.Contains(t));
    }
}

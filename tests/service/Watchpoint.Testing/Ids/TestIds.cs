// Deterministic ids for tests (spec TST §4.2). One method per UUID id type of Watchpoint.Kernel.Ids;
// keep in sync with src/service/Watchpoint.Kernel/Ids/EntityIds.cs.
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Testing.Ids;

/// <summary>
/// Deterministic, version-7 shaped ids: <c>TestIds.Device(1)</c> is <c>00000000-0000-7000-8000-000000000001</c>.
/// The sequence number is written in decimal digits so failures stay readable.
/// </summary>
public static class TestIds
{
    /// <summary>The largest supported sequence number.</summary>
    public const long MaxSequence = 999_999_999_999;

    /// <summary>Returns the deterministic UUID for <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number, 0 to <see cref="MaxSequence"/>.</param>
    /// <returns>The UUID <c>00000000-0000-7000-8000-nnnnnnnnnnnn</c>.</returns>
    public static Guid Uuid(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, MaxSequence);
        return Guid.Parse($"00000000-0000-7000-8000-{n:D12}");
    }

    /// <summary>Returns the deterministic <see cref="SiteId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static SiteId Site(long n) => SiteId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="NodeId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static NodeId Node(long n) => NodeId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ServerId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ServerId Server(long n) => ServerId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="DeviceId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static DeviceId Device(long n) => DeviceId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="LocationId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static LocationId Location(long n) => LocationId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="TagId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static TagId Tag(long n) => TagId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="TagFamilyId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static TagFamilyId TagFamily(long n) => TagFamilyId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="LinkId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static LinkId Link(long n) => LinkId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ConnectionId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ConnectionId Connection(long n) => ConnectionId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="HostId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static HostId Host(long n) => HostId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="UserId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static UserId User(long n) => UserId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="UserGroupId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static UserGroupId UserGroup(long n) => UserGroupId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="SessionId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static SessionId Session(long n) => SessionId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="RightGrantId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static RightGrantId RightGrant(long n) => RightGrantId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="DraftId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static DraftId Draft(long n) => DraftId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ChangeSetId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ChangeSetId ChangeSet(long n) => ChangeSetId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="CustomFieldId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static CustomFieldId CustomField(long n) => CustomFieldId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="HolidaySetId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static HolidaySetId HolidaySet(long n) => HolidaySetId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="WorkstationId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static WorkstationId Workstation(long n) => WorkstationId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="AlarmId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static AlarmId Alarm(long n) => AlarmId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="AlarmRuleId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static AlarmRuleId AlarmRule(long n) => AlarmRuleId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ProcedureId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ProcedureId Procedure(long n) => ProcedureId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="EventRecordId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static EventRecordId EventRecord(long n) => EventRecordId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="CommandId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static CommandId Command(long n) => CommandId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="CalendarId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static CalendarId Calendar(long n) => CalendarId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="SynopticId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static SynopticId Synoptic(long n) => SynopticId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="AutomationId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static AutomationId Automation(long n) => AutomationId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="AutomationRunId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static AutomationRunId AutomationRun(long n) => AutomationRunId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ScriptId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ScriptId Script(long n) => ScriptId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="NotificationId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static NotificationId Notification(long n) => NotificationId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ReportId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ReportId Report(long n) => ReportId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="FileId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static FileId File(long n) => FileId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="SecretId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static SecretId Secret(long n) => SecretId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="LicenceId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static LicenceId Licence(long n) => LicenceId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="FederationLinkId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static FederationLinkId FederationLink(long n) => FederationLinkId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="ObservationId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static ObservationId Observation(long n) => ObservationId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="JobId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static JobId Job(long n) => JobId.From(Uuid(n));

    /// <summary>Returns the deterministic <see cref="IncidentId"/> number <paramref name="n"/>.</summary>
    /// <param name="n">The sequence number.</param>
    /// <returns>The id.</returns>
    public static IncidentId Incident(long n) => IncidentId.From(Uuid(n));
}

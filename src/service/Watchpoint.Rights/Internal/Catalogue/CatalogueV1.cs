using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Catalogue;

/// <summary>The explicit permissions of catalogue v1 (spec RGT §4.1); command permissions are generated.</summary>
internal static class CatalogueV1
{
    private static readonly IReadOnlySet<ConstraintKind> NoConstraints = new HashSet<ConstraintKind>();

    public static IEnumerable<PermissionDefinition> Definitions()
    {
        foreach (var code in new[] { "operate.devices.view", "operate.personalData.view", "operate.history.view", "operate.events.view", "operate.events.export" })
        {
            yield return Def(code, "operation.devices", "rights", PermissionScopeKind.Devices, sensitive: code == "operate.personalData.view");
        }

        yield return Def("operate.notes.create", "operation.devices", "rights", PermissionScopeKind.Locations);

        yield return Def("alarms.view", "operation.alarms", "alarms", PermissionScopeKind.Devices, ConstraintKind.PriorityRange);
        foreach (var code in new[] { "alarms.acknowledge", "alarms.comment", "alarms.transfer", "alarms.close", "alarms.closeWithoutReport" })
        {
            yield return Def(code, "operation.alarms", "alarms", PermissionScopeKind.Devices, sensitive: code == "alarms.closeWithoutReport");
        }

        yield return Def("alarms.shelve", "operation.alarms", "alarms", PermissionScopeKind.Devices, ConstraintKind.MaxDuration);
        yield return Def("alarms.mute", "operation.alarms", "alarms", PermissionScopeKind.None);
        yield return Def("admin.systemEvents.view", "operation.alarms", "alarms", PermissionScopeKind.System);
        yield return Def("operate.notifications.subscribe", "operation.alarms", "notifications", PermissionScopeKind.None);

        foreach (var code in new[] { "commands.approve", "commands.viewAll", "commands.cancelAny" })
        {
            yield return Def(code, "operation.commands", "commands", PermissionScopeKind.Devices);
        }

        yield return Def("commands.priority.critical", "operation.commands", "commands", PermissionScopeKind.None, sensitive: true);

        yield return Def("operate.workspaces.save", "operation.workspace", "rights", PermissionScopeKind.None);
        yield return Def("operate.workspaces.publish", "operation.workspace", "rights", PermissionScopeKind.None);
        yield return Def("operate.duringMaintenance", "operation.workspace", "maintenance", PermissionScopeKind.None, sensitive: true);

        foreach (var code in new[] { "config.registry.view", "config.registry.edit", "config.alarms.view", "config.alarms.edit" })
        {
            yield return Def(code, "configuration", "configuration", PermissionScopeKind.Locations);
        }

        foreach (var code in new[]
        {
            "config.integrations.view", "config.integrations.edit", "config.integrations.install", "config.integrations.operate",
            "config.calendars.edit", "config.users.view", "config.users.edit", "config.rights.view", "config.rights.edit",
            "config.publish", "config.approve", "config.locks.break", "config.maintenance",
        })
        {
            yield return Def(code, "configuration", "configuration", PermissionScopeKind.None, sensitive: code is "config.rights.edit" or "config.users.edit");
        }

        foreach (var code in new[] { "admin.audit.view", "admin.sessions.view", "admin.sessions.manage", "admin.health.view", "admin.diagnostics.export", "admin.privacy" })
        {
            yield return Def(code, "administration", "administration", PermissionScopeKind.None, sensitive: code == "admin.privacy");
        }
    }

    /// <summary>The definition of a generated command permission <c>commands.&lt;capability&gt;.&lt;command&gt;</c>.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The definition.</returns>
    public static PermissionDefinition Command(PermissionCode code) => new(code, "operation.commands", "commands",
        "rights.permission." + code.Value + ".label", "rights.permission." + code.Value + ".description", PermissionScopeKind.Devices,
        new HashSet<ConstraintKind> { ConstraintKind.RequireReason, ConstraintKind.RequireFourEyes, ConstraintKind.RequireStepUp, ConstraintKind.ClientKinds }, false);

    private static PermissionDefinition Def(string code, string family, string module, PermissionScopeKind kind, ConstraintKind? constraint = null, bool sensitive = false) =>
        new(PermissionCode.FromTrusted(code), family, module, "rights.permission." + code + ".label", "rights.permission." + code + ".description", kind,
            constraint is { } c ? new HashSet<ConstraintKind> { c } : NoConstraints, sensitive);
}

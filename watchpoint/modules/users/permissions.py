from watchpoint.core.permissions import register

USERS_READ = register("users.read", "View user accounts", module="users")
USERS_MANAGE = register("users.manage", "Create, edit, disable and delete user accounts", module="users")
GROUPS_READ = register("groups.read", "View user groups and their rights", module="users")
GROUPS_MANAGE = register("groups.manage", "Create, edit and delete user groups and assign rights", module="users")
AUDIT_READ = register("audit.read", "Consult the operator action journal", module="users")

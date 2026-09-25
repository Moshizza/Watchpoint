"""Users module: operator accounts, groups (profiles), rights, authentication and action journal."""

from watchpoint.modules.users import models, permissions, service  # noqa: F401  (registers tables and permission codes)
from watchpoint.modules.users.router import router

__all__ = ["router"]

"""Central registry of permission codes.

Each module declares the permissions it checks at import time, e.g.::

    USERS_READ = register("users.read", "View users", module="users")

Groups can only be granted registered codes, and ``GET /api/permissions``
exposes the registry so client UIs can build their rights editors from it.
"""

from dataclasses import dataclass


@dataclass(frozen=True)
class Permission:
    code: str
    description: str
    module: str


_registry: dict[str, Permission] = {}


def register(code: str, description: str, module: str) -> str:
    existing = _registry.get(code)
    if existing and existing != Permission(code, description, module):
        raise ValueError(f"permission {code!r} already registered differently by module {existing.module!r}")
    _registry[code] = Permission(code, description, module)
    return code


def is_registered(code: str) -> bool:
    return code in _registry


def all_permissions() -> list[Permission]:
    return sorted(_registry.values(), key=lambda p: p.code)

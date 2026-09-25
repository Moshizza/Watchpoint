"""Business rules of the users module, independent of the HTTP layer."""

import logging
import secrets
from datetime import timedelta

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from watchpoint.core import audit
from watchpoint.core.config import Settings
from watchpoint.core.database import utcnow
from watchpoint.core.permissions import is_registered
from watchpoint.core.security import hash_password, needs_rehash, verify_password
from watchpoint.modules.users.models import Group, GroupPermission, User
from watchpoint.modules.users.schemas import GroupCreate, GroupUpdate, UserCreate, UserUpdate

log = logging.getLogger(__name__)


class UsersError(Exception):
    pass


class NotFoundError(UsersError):
    pass


class ConflictError(UsersError):
    pass


class ForbiddenError(UsersError):
    pass


class InvalidDataError(UsersError):
    pass


class AuthenticationError(UsersError):
    pass


# --- Passwords -----------------------------------------------------------------


def check_password_policy(settings: Settings, password: str, login: str | None = None) -> None:
    problems = []
    if len(password) < settings.password_min_length:
        problems.append(f"must be at least {settings.password_min_length} characters long")
    classes = sum(
        [
            any(c.islower() for c in password),
            any(c.isupper() for c in password),
            any(c.isdigit() for c in password),
            any(not c.isalnum() for c in password),
        ]
    )
    if classes < 3:
        problems.append("must mix at least 3 of: lowercase, uppercase, digits, symbols")
    if login and login.lower() in password.lower():
        problems.append("must not contain the login")
    if problems:
        raise InvalidDataError("Password " + "; ".join(problems))


def _set_password(user: User, password: str, must_change: bool) -> None:
    user.password_hash = hash_password(password)
    user.password_changed_at = utcnow()
    user.must_change_password = must_change
    user.invalidate_tokens()


# --- Authentication ------------------------------------------------------------


def authenticate(session: Session, settings: Settings, login: str, password: str, client_ip: str | None) -> User:
    """Checks credentials, applying account lockout. Always commits (to persist counters and journal)."""
    user = session.scalar(select(User).where(func.lower(User.login) == login.lower()))
    password_ok = verify_password(password, user.password_hash if user else None)

    def fail(reason: str) -> AuthenticationError:
        audit.record(
            session, "auth.login_failed", actor=user, actor_login=login, details={"reason": reason}, client_ip=client_ip
        )
        session.commit()
        # Same message whatever the reason, so logins cannot be enumerated.
        return AuthenticationError("Invalid login or password")

    if user is None:
        raise fail("unknown_login")
    if user.is_locked:
        raise fail("locked")
    if not password_ok:
        user.failed_login_count += 1
        if user.failed_login_count >= settings.max_failed_logins:
            user.locked_until = utcnow() + timedelta(minutes=settings.lockout_minutes)
            user.failed_login_count = 0
            audit.record(session, "user.locked", actor=user, target_type="user", target_id=user.id, client_ip=client_ip)
        raise fail("bad_password")
    if not user.is_enabled:
        raise fail("disabled")

    user.failed_login_count = 0
    user.locked_until = None
    user.last_login_at = utcnow()
    if needs_rehash(user.password_hash):
        user.password_hash = hash_password(password)
    audit.record(session, "auth.login", actor=user, client_ip=client_ip)
    session.commit()
    return user


def change_own_password(
    session: Session, settings: Settings, user: User, current: str, new: str, client_ip: str | None
) -> None:
    if not verify_password(current, user.password_hash):
        raise AuthenticationError("Current password is incorrect")
    if current == new:
        raise InvalidDataError("New password must differ from the current one")
    check_password_policy(settings, new, user.login)
    _set_password(user, new, must_change=False)
    audit.record(
        session, "user.password_changed", actor=user, target_type="user", target_id=user.id, client_ip=client_ip
    )
    session.commit()


# --- Anti privilege-escalation guards -------------------------------------------


def _ensure_can_grant_permissions(actor: User, codes: set[str]) -> None:
    """A non-superuser can only hand out rights they hold themselves."""
    if actor.is_superuser:
        return
    missing = codes - actor.permissions
    if missing:
        raise ForbiddenError(f"Cannot grant permissions you do not hold: {', '.join(sorted(missing))}")


def _ensure_can_manage_user(actor: User, target: User) -> None:
    if target.is_superuser and not actor.is_superuser:
        raise ForbiddenError("Only a superuser can modify a superuser account")


def _active_superuser_count(session: Session) -> int:
    return session.scalar(
        select(func.count()).select_from(User).where(User.is_superuser.is_(True), User.is_enabled.is_(True))
    )


def _ensure_not_last_superuser(session: Session, target: User) -> None:
    if target.is_superuser and target.is_enabled and _active_superuser_count(session) <= 1:
        raise ConflictError("At least one enabled superuser account must remain")


# --- Users ---------------------------------------------------------------------


def list_users(session: Session) -> list[User]:
    return list(session.scalars(select(User).order_by(User.login)))


def get_user(session: Session, user_id: int) -> User:
    user = session.get(User, user_id)
    if user is None:
        raise NotFoundError(f"User {user_id} not found")
    return user


def _load_groups(session: Session, group_ids: list[int]) -> list[Group]:
    wanted = set(group_ids)
    groups = list(session.scalars(select(Group).where(Group.id.in_(wanted)))) if wanted else []
    missing = wanted - {g.id for g in groups}
    if missing:
        raise InvalidDataError(f"Unknown group ids: {sorted(missing)}")
    return groups


def _login_taken(session: Session, login: str) -> bool:
    return session.scalar(select(User.id).where(func.lower(User.login) == login.lower())) is not None


def create_user(session: Session, settings: Settings, actor: User, data: UserCreate, client_ip: str | None) -> User:
    if data.is_superuser and not actor.is_superuser:
        raise ForbiddenError("Only a superuser can create a superuser account")
    if _login_taken(session, data.login):
        raise ConflictError(f"Login {data.login!r} is already used")
    check_password_policy(settings, data.password, data.login)
    groups = _load_groups(session, data.group_ids)
    _ensure_can_grant_permissions(actor, {p.code for g in groups for p in g.permissions})

    user = User(
        login=data.login,
        full_name=data.full_name,
        email=data.email,
        phone=data.phone,
        language=data.language,
        is_superuser=data.is_superuser,
        is_enabled=data.is_enabled,
        groups=groups,
    )
    _set_password(user, data.password, must_change=data.must_change_password)
    session.add(user)
    session.flush()
    audit.record(
        session,
        "user.created",
        actor=actor,
        target_type="user",
        target_id=user.id,
        details={"login": user.login, "groups": [g.name for g in groups], "is_superuser": user.is_superuser},
        client_ip=client_ip,
    )
    session.commit()
    return user


def update_user(session: Session, actor: User, user_id: int, data: UserUpdate, client_ip: str | None) -> User:
    user = get_user(session, user_id)
    _ensure_can_manage_user(actor, user)
    changes = data.model_dump(exclude_unset=True)

    if changes.get("is_superuser") is not None and changes["is_superuser"] != user.is_superuser:
        if not actor.is_superuser:
            raise ForbiddenError("Only a superuser can change superuser status")
        if user.id == actor.id:
            raise ForbiddenError("You cannot change your own superuser status")
        if not changes["is_superuser"]:
            _ensure_not_last_superuser(session, user)
    if changes.get("is_enabled") is False and user.is_enabled:
        if user.id == actor.id:
            raise ForbiddenError("You cannot disable your own account")
        _ensure_not_last_superuser(session, user)
        user.invalidate_tokens()

    if "group_ids" in changes:
        group_ids = changes.pop("group_ids")
        if group_ids is not None:
            groups = _load_groups(session, group_ids)
            added = {g.id for g in groups} - {g.id for g in user.groups}
            _ensure_can_grant_permissions(actor, {p.code for g in groups if g.id in added for p in g.permissions})
            user.groups = groups
            changes["groups"] = sorted(g.name for g in groups)

    for field in ("full_name", "email", "phone", "language", "is_superuser", "is_enabled", "must_change_password"):
        if field in changes and (changes[field] is not None or field in ("email", "phone")):
            setattr(user, field, changes[field])

    audit.record(
        session,
        "user.updated",
        actor=actor,
        target_type="user",
        target_id=user.id,
        details={"changes": changes},
        client_ip=client_ip,
    )
    session.commit()
    return user


def delete_user(session: Session, actor: User, user_id: int, client_ip: str | None) -> None:
    user = get_user(session, user_id)
    if user.id == actor.id:
        raise ForbiddenError("You cannot delete your own account")
    _ensure_can_manage_user(actor, user)
    _ensure_not_last_superuser(session, user)
    audit.record(
        session,
        "user.deleted",
        actor=actor,
        target_type="user",
        target_id=user.id,
        details={"login": user.login},
        client_ip=client_ip,
    )
    session.delete(user)
    session.commit()


def reset_password(
    session: Session,
    settings: Settings,
    actor: User,
    user_id: int,
    new_password: str,
    must_change: bool,
    client_ip: str | None,
) -> User:
    user = get_user(session, user_id)
    _ensure_can_manage_user(actor, user)
    check_password_policy(settings, new_password, user.login)
    _set_password(user, new_password, must_change=must_change)
    audit.record(
        session, "user.password_reset", actor=actor, target_type="user", target_id=user.id, client_ip=client_ip
    )
    session.commit()
    return user


def unlock_user(session: Session, actor: User, user_id: int, client_ip: str | None) -> User:
    user = get_user(session, user_id)
    _ensure_can_manage_user(actor, user)
    user.locked_until = None
    user.failed_login_count = 0
    audit.record(session, "user.unlocked", actor=actor, target_type="user", target_id=user.id, client_ip=client_ip)
    session.commit()
    return user


# --- Groups --------------------------------------------------------------------


def _validate_permission_codes(codes: list[str]) -> set[str]:
    unknown = sorted(c for c in codes if not is_registered(c))
    if unknown:
        raise InvalidDataError(f"Unknown permissions: {', '.join(unknown)}")
    return set(codes)


def list_groups(session: Session) -> list[Group]:
    return list(session.scalars(select(Group).order_by(Group.name)))


def get_group(session: Session, group_id: int) -> Group:
    group = session.get(Group, group_id)
    if group is None:
        raise NotFoundError(f"Group {group_id} not found")
    return group


def _group_name_taken(session: Session, name: str, exclude_id: int | None = None) -> bool:
    query = select(Group.id).where(func.lower(Group.name) == name.lower())
    if exclude_id is not None:
        query = query.where(Group.id != exclude_id)
    return session.scalar(query) is not None


def create_group(session: Session, actor: User, data: GroupCreate, client_ip: str | None) -> Group:
    if _group_name_taken(session, data.name):
        raise ConflictError(f"Group {data.name!r} already exists")
    codes = _validate_permission_codes(data.permissions)
    _ensure_can_grant_permissions(actor, codes)
    group = Group(
        name=data.name,
        description=data.description,
        permissions=[GroupPermission(code=c) for c in sorted(codes)],
    )
    session.add(group)
    session.flush()
    audit.record(
        session,
        "group.created",
        actor=actor,
        target_type="group",
        target_id=group.id,
        details={"name": group.name, "permissions": sorted(codes)},
        client_ip=client_ip,
    )
    session.commit()
    return group


def update_group(session: Session, actor: User, group_id: int, data: GroupUpdate, client_ip: str | None) -> Group:
    group = get_group(session, group_id)
    changes = data.model_dump(exclude_unset=True, exclude_none=True)

    if "name" in changes:
        if _group_name_taken(session, changes["name"], exclude_id=group.id):
            raise ConflictError(f"Group {changes['name']!r} already exists")
        group.name = changes["name"]
    if "description" in changes:
        group.description = changes["description"]
    if "permissions" in changes:
        codes = _validate_permission_codes(changes["permissions"])
        current = set(group.permission_codes)
        _ensure_can_grant_permissions(actor, codes - current)
        # Removing rights from a group that holds rights the actor lacks would
        # also let them reshape a more privileged profile.
        _ensure_can_grant_permissions(actor, current - codes)
        existing = {p.code: p for p in group.permissions}
        group.permissions = [existing.get(c) or GroupPermission(code=c) for c in sorted(codes)]
        changes["permissions"] = sorted(codes)

    audit.record(
        session,
        "group.updated",
        actor=actor,
        target_type="group",
        target_id=group.id,
        details={"changes": changes},
        client_ip=client_ip,
    )
    session.commit()
    return group


def delete_group(session: Session, actor: User, group_id: int, client_ip: str | None) -> None:
    group = get_group(session, group_id)
    _ensure_can_grant_permissions(actor, set(group.permission_codes))
    audit.record(
        session,
        "group.deleted",
        actor=actor,
        target_type="group",
        target_id=group.id,
        details={"name": group.name},
        client_ip=client_ip,
    )
    session.delete(group)
    session.commit()


# --- Bootstrap -----------------------------------------------------------------


def bootstrap_admin(session: Session, settings: Settings) -> str | None:
    """Creates the initial superuser when the database has no user.

    Returns the generated password when one had to be generated, so it can be
    shown once to whoever installs the system.
    """
    if session.scalar(select(func.count()).select_from(User)):
        return None
    generated = None
    password = settings.bootstrap_admin_password
    if not password:
        password = generated = secrets.token_urlsafe(12) + "-Aa1"
    user = User(login=settings.bootstrap_admin_login, full_name="Administrator", is_superuser=True)
    _set_password(user, password, must_change=True)
    session.add(user)
    session.flush()
    audit.record(session, "user.bootstrap", actor_login="system", target_type="user", target_id=user.id)
    session.commit()
    log.info("Created bootstrap administrator %r", user.login)
    return generated

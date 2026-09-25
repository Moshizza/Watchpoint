from collections.abc import Callable
from datetime import UTC, datetime
from typing import Annotated, TypeVar

from fastapi import APIRouter, Depends, HTTPException, Query, Response, status
from sqlalchemy import select

from watchpoint.core.audit import AuditEvent
from watchpoint.core.permissions import all_permissions
from watchpoint.core.security import create_access_token
from watchpoint.modules.users import service
from watchpoint.modules.users.deps import (
    ClientIpDep,
    CurrentUser,
    SessionDep,
    SettingsDep,
    get_authenticated_user,
    require_permission,
)
from watchpoint.modules.users.models import User
from watchpoint.modules.users.permissions import AUDIT_READ, GROUPS_MANAGE, GROUPS_READ, USERS_MANAGE, USERS_READ
from watchpoint.modules.users.schemas import (
    AuditEventRead,
    CurrentUserRead,
    GroupCreate,
    GroupRead,
    GroupUpdate,
    LoginRequest,
    PasswordChange,
    PasswordReset,
    PermissionRead,
    TokenResponse,
    UserCreate,
    UserRead,
    UserUpdate,
)

_ERROR_STATUS: dict[type[service.UsersError], int] = {
    service.NotFoundError: status.HTTP_404_NOT_FOUND,
    service.ConflictError: status.HTTP_409_CONFLICT,
    service.ForbiddenError: status.HTTP_403_FORBIDDEN,
    service.InvalidDataError: 422,
    service.AuthenticationError: status.HTTP_401_UNAUTHORIZED,
}


T = TypeVar("T")


def _call(fn: Callable[..., T], *args, **kwargs) -> T:
    """Runs a service function, translating domain errors into HTTP errors."""
    try:
        return fn(*args, **kwargs)
    except service.UsersError as exc:
        raise HTTPException(_ERROR_STATUS[type(exc)], str(exc)) from exc


UsersReader = Annotated[User, Depends(require_permission(USERS_READ))]
UsersManager = Annotated[User, Depends(require_permission(USERS_MANAGE))]
GroupsReader = Annotated[User, Depends(require_permission(GROUPS_READ))]
GroupsManager = Annotated[User, Depends(require_permission(GROUPS_MANAGE))]
AuditReader = Annotated[User, Depends(require_permission(AUDIT_READ))]


# --- Authentication ------------------------------------------------------------

auth_router = APIRouter(prefix="/auth", tags=["auth"])


@auth_router.post("/login", response_model=TokenResponse)
def login(body: LoginRequest, session: SessionDep, settings: SettingsDep, ip: ClientIpDep):
    user = _call(service.authenticate, session, settings, body.login, body.password, ip)
    token, expires_in = create_access_token(settings, user.id, user.token_version)
    return TokenResponse(access_token=token, expires_in=expires_in, must_change_password=user.must_change_password)


@auth_router.get("/me", response_model=CurrentUserRead)
def me(user: Annotated[User, Depends(get_authenticated_user)]):
    return user


@auth_router.post("/me/password", response_model=TokenResponse)
def change_my_password(
    body: PasswordChange,
    user: Annotated[User, Depends(get_authenticated_user)],
    session: SessionDep,
    settings: SettingsDep,
    ip: ClientIpDep,
):
    """Changes the caller's password. Other sessions are revoked, so a fresh token is returned."""
    _call(service.change_own_password, session, settings, user, body.current_password, body.new_password, ip)
    token, expires_in = create_access_token(settings, user.id, user.token_version)
    return TokenResponse(access_token=token, expires_in=expires_in, must_change_password=False)


# --- Users ---------------------------------------------------------------------

users_router = APIRouter(prefix="/users", tags=["users"])


@users_router.get("", response_model=list[UserRead])
def list_users(session: SessionDep, _: UsersReader):
    return service.list_users(session)


@users_router.post("", response_model=UserRead, status_code=status.HTTP_201_CREATED)
def create_user(body: UserCreate, session: SessionDep, settings: SettingsDep, actor: UsersManager, ip: ClientIpDep):
    return _call(service.create_user, session, settings, actor, body, ip)


@users_router.get("/{user_id}", response_model=UserRead)
def get_user(user_id: int, session: SessionDep, _: UsersReader):
    return _call(service.get_user, session, user_id)


@users_router.patch("/{user_id}", response_model=UserRead)
def update_user(user_id: int, body: UserUpdate, session: SessionDep, actor: UsersManager, ip: ClientIpDep):
    return _call(service.update_user, session, actor, user_id, body, ip)


@users_router.delete("/{user_id}", status_code=status.HTTP_204_NO_CONTENT)
def delete_user(user_id: int, session: SessionDep, actor: UsersManager, ip: ClientIpDep):
    _call(service.delete_user, session, actor, user_id, ip)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@users_router.post("/{user_id}/password", response_model=UserRead)
def reset_password(
    user_id: int,
    body: PasswordReset,
    session: SessionDep,
    settings: SettingsDep,
    actor: UsersManager,
    ip: ClientIpDep,
):
    return _call(
        service.reset_password, session, settings, actor, user_id, body.new_password, body.must_change_password, ip
    )


@users_router.post("/{user_id}/unlock", response_model=UserRead)
def unlock_user(user_id: int, session: SessionDep, actor: UsersManager, ip: ClientIpDep):
    return _call(service.unlock_user, session, actor, user_id, ip)


# --- Groups --------------------------------------------------------------------

groups_router = APIRouter(prefix="/groups", tags=["groups"])


@groups_router.get("", response_model=list[GroupRead])
def list_groups(session: SessionDep, _: GroupsReader):
    return service.list_groups(session)


@groups_router.post("", response_model=GroupRead, status_code=status.HTTP_201_CREATED)
def create_group(body: GroupCreate, session: SessionDep, actor: GroupsManager, ip: ClientIpDep):
    return _call(service.create_group, session, actor, body, ip)


@groups_router.get("/{group_id}", response_model=GroupRead)
def get_group(group_id: int, session: SessionDep, _: GroupsReader):
    return _call(service.get_group, session, group_id)


@groups_router.patch("/{group_id}", response_model=GroupRead)
def update_group(group_id: int, body: GroupUpdate, session: SessionDep, actor: GroupsManager, ip: ClientIpDep):
    return _call(service.update_group, session, actor, group_id, body, ip)


@groups_router.delete("/{group_id}", status_code=status.HTTP_204_NO_CONTENT)
def delete_group(group_id: int, session: SessionDep, actor: GroupsManager, ip: ClientIpDep):
    _call(service.delete_group, session, actor, group_id, ip)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


# --- Permissions & journal -----------------------------------------------------

misc_router = APIRouter(tags=["permissions", "audit"])


@misc_router.get("/permissions", response_model=list[PermissionRead])
def list_permissions(_: CurrentUser):
    return all_permissions()


@misc_router.get("/audit", response_model=list[AuditEventRead])
def list_audit_events(
    session: SessionDep,
    _: AuditReader,
    action: str | None = None,
    actor_id: int | None = None,
    since: datetime | None = None,
    limit: Annotated[int, Query(ge=1, le=1000)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
):
    query = select(AuditEvent).order_by(AuditEvent.id.desc())
    if action:
        query = query.where(AuditEvent.action == action)
    if actor_id is not None:
        query = query.where(AuditEvent.actor_id == actor_id)
    if since is not None:
        if since.tzinfo is None:
            since = since.replace(tzinfo=UTC)
        query = query.where(AuditEvent.timestamp >= since)
    return list(session.scalars(query.limit(limit).offset(offset)))


router = APIRouter()
for _r in (auth_router, users_router, groups_router, misc_router):
    router.include_router(_r)

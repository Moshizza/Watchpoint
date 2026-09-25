"""FastAPI dependencies for authentication and permission checks."""

from collections.abc import Callable
from typing import Annotated

from fastapi import Depends, HTTPException, Request, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from watchpoint.core.config import Settings, get_settings
from watchpoint.core.database import get_session
from watchpoint.core.security import TokenError, decode_access_token
from watchpoint.modules.users.models import User

_bearer = HTTPBearer(auto_error=False)

SessionDep = Annotated[Session, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings)]


def client_ip(request: Request) -> str | None:
    return request.client.host if request.client else None


ClientIpDep = Annotated[str | None, Depends(client_ip)]


def _unauthorized(detail: str = "Not authenticated") -> HTTPException:
    return HTTPException(status.HTTP_401_UNAUTHORIZED, detail, headers={"WWW-Authenticate": "Bearer"})


def get_authenticated_user(
    session: SessionDep,
    settings: SettingsDep,
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(_bearer)],
) -> User:
    """Resolves the bearer token, even if the user still has to change their password."""
    if credentials is None:
        raise _unauthorized()
    try:
        user_id, version = decode_access_token(settings, credentials.credentials)
    except TokenError:
        raise _unauthorized("Invalid or expired token") from None
    user = session.get(User, user_id)
    if user is None or not user.is_enabled or user.token_version != version:
        raise _unauthorized("Invalid or expired token")
    return user


def get_current_user(user: Annotated[User, Depends(get_authenticated_user)]) -> User:
    """Like :func:`get_authenticated_user`, but blocks accounts that must change their password first."""
    if user.must_change_password:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "Password change required")
    return user


CurrentUser = Annotated[User, Depends(get_current_user)]


def require_permission(code: str) -> Callable[..., User]:
    """Dependency factory: ``user: Annotated[User, Depends(require_permission("users.read"))]``."""

    def checker(user: CurrentUser) -> User:
        if not user.has_permission(code):
            raise HTTPException(status.HTTP_403_FORBIDDEN, f"Missing permission: {code}")
        return user

    return checker

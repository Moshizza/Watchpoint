"""Password hashing and access-token handling."""

from datetime import timedelta

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError

from watchpoint.core.config import Settings
from watchpoint.core.database import utcnow

_hasher = PasswordHasher()
_ALGORITHM = "HS256"

# Verified against when a login does not exist, so unknown and known logins
# take the same time to reject.
_DUMMY_HASH = _hasher.hash("watchpoint-dummy-password")


def hash_password(password: str) -> str:
    return _hasher.hash(password)


def verify_password(password: str, password_hash: str | None) -> bool:
    try:
        return _hasher.verify(password_hash or _DUMMY_HASH, password) and password_hash is not None
    except (VerificationError, InvalidHashError):
        return False


def needs_rehash(password_hash: str) -> bool:
    return _hasher.check_needs_rehash(password_hash)


class TokenError(Exception):
    pass


def create_access_token(settings: Settings, user_id: int, token_version: int) -> tuple[str, int]:
    """Returns the encoded token and its lifetime in seconds."""
    lifetime = timedelta(minutes=settings.access_token_minutes)
    now = utcnow()
    payload = {"sub": str(user_id), "ver": token_version, "iat": now, "exp": now + lifetime}
    return jwt.encode(payload, settings.secret_key, algorithm=_ALGORITHM), int(lifetime.total_seconds())


def decode_access_token(settings: Settings, token: str) -> tuple[int, int]:
    """Returns ``(user_id, token_version)`` or raises :class:`TokenError`."""
    try:
        payload = jwt.decode(token, settings.secret_key, algorithms=[_ALGORITHM], options={"require": ["sub", "exp"]})
        return int(payload["sub"]), int(payload.get("ver", 0))
    except (jwt.PyJWTError, ValueError, KeyError) as exc:
        raise TokenError(str(exc)) from exc

"""Application settings, loaded from environment variables prefixed ``WATCHPOINT_``."""

import logging
import secrets
from functools import lru_cache

from pydantic import Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

log = logging.getLogger(__name__)


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="WATCHPOINT_", env_file=".env", extra="ignore")

    database_url: str = "sqlite:///./watchpoint.db"

    # JWT signing key. When unset a random key is generated, which means
    # every restart invalidates all issued tokens - fine for development only.
    secret_key: str = ""
    access_token_minutes: int = 60 * 8  # one operator shift

    # Account lockout after repeated failed logins.
    max_failed_logins: int = 5
    lockout_minutes: int = 15

    # Password policy.
    password_min_length: int = 10

    # Bootstrap administrator, created on first start when no user exists.
    bootstrap_admin_login: str = "admin"
    bootstrap_admin_password: str = Field(default="", repr=False)

    @model_validator(mode="after")
    def _ensure_secret(self) -> "Settings":
        if not self.secret_key:
            log.warning("WATCHPOINT_SECRET_KEY is not set; using a random key (tokens will not survive restarts)")
            self.secret_key = secrets.token_urlsafe(48)
        return self


@lru_cache
def get_settings() -> Settings:
    return Settings()

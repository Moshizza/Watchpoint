from datetime import datetime

from pydantic import BaseModel, ConfigDict, EmailStr, Field, field_validator

LOGIN_PATTERN = r"^[A-Za-z0-9._@-]+$"


class GroupRef(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    name: str


class UserBase(BaseModel):
    full_name: str = Field(default="", max_length=128)
    email: EmailStr | None = None
    phone: str | None = Field(default=None, max_length=32)
    language: str = Field(default="en", min_length=2, max_length=8)


class UserCreate(UserBase):
    login: str = Field(min_length=2, max_length=64, pattern=LOGIN_PATTERN)
    password: str = Field(max_length=256)
    is_superuser: bool = False
    is_enabled: bool = True
    must_change_password: bool = True
    group_ids: list[int] = []


class UserUpdate(BaseModel):
    """Partial update: only the fields that are sent are changed."""

    full_name: str | None = Field(default=None, max_length=128)
    email: EmailStr | None = None
    phone: str | None = Field(default=None, max_length=32)
    language: str | None = Field(default=None, min_length=2, max_length=8)
    is_superuser: bool | None = None
    is_enabled: bool | None = None
    must_change_password: bool | None = None
    group_ids: list[int] | None = None


class UserRead(UserBase):
    model_config = ConfigDict(from_attributes=True)

    id: int
    login: str
    is_superuser: bool
    is_enabled: bool
    is_locked: bool
    must_change_password: bool
    last_login_at: datetime | None
    created_at: datetime
    groups: list[GroupRef]


class CurrentUserRead(UserRead):
    permissions: list[str]

    @field_validator("permissions", mode="before")
    @classmethod
    def _sorted(cls, value):
        return sorted(value)


class PasswordReset(BaseModel):
    new_password: str = Field(max_length=256)
    must_change_password: bool = True


class PasswordChange(BaseModel):
    current_password: str = Field(max_length=256)
    new_password: str = Field(max_length=256)


class GroupCreate(BaseModel):
    name: str = Field(min_length=1, max_length=64)
    description: str = ""
    permissions: list[str] = []


class GroupUpdate(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=64)
    description: str | None = None
    permissions: list[str] | None = None


class GroupRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    name: str
    description: str
    permissions: list[str] = Field(validation_alias="permission_codes")
    created_at: datetime


class PermissionRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    code: str
    description: str
    module: str


class LoginRequest(BaseModel):
    login: str = Field(max_length=64)
    password: str = Field(max_length=256)


class TokenResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"
    expires_in: int
    must_change_password: bool


class AuditEventRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    timestamp: datetime
    actor_id: int | None
    actor_login: str | None
    action: str
    target_type: str | None
    target_id: str | None
    details: dict
    client_ip: str | None

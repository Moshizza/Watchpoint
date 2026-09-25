"""Operator action journal.

Every security-relevant action (logins, rights changes, ...) is recorded
here so that supervisors can later answer "who did what, and when".
"""

from datetime import datetime

from sqlalchemy import JSON, Integer, String
from sqlalchemy.orm import Mapped, Session, mapped_column

from watchpoint.core.database import Base, UTCDateTime, utcnow


class AuditEvent(Base):
    __tablename__ = "audit_events"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    timestamp: Mapped[datetime] = mapped_column(UTCDateTime, default=utcnow, index=True)
    # Kept as plain values (no cascading FK) so the journal survives user deletion.
    actor_id: Mapped[int | None] = mapped_column(Integer, index=True)
    actor_login: Mapped[str | None] = mapped_column(String(64))
    action: Mapped[str] = mapped_column(String(64), index=True)
    target_type: Mapped[str | None] = mapped_column(String(32))
    target_id: Mapped[str | None] = mapped_column(String(64))
    details: Mapped[dict] = mapped_column(JSON, default=dict)
    client_ip: Mapped[str | None] = mapped_column(String(45))


def record(
    session: Session,
    action: str,
    *,
    actor=None,
    actor_login: str | None = None,
    target_type: str | None = None,
    target_id: object = None,
    details: dict | None = None,
    client_ip: str | None = None,
) -> AuditEvent:
    """Adds an audit event to ``session``; it is committed with the caller's transaction."""
    event = AuditEvent(
        actor_id=getattr(actor, "id", None),
        actor_login=getattr(actor, "login", None) or actor_login,
        action=action,
        target_type=target_type,
        target_id=str(target_id) if target_id is not None else None,
        details=details or {},
        client_ip=client_ip,
    )
    session.add(event)
    return event

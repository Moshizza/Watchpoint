"""Application factory. Run with: ``uvicorn watchpoint.main:app``."""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from watchpoint import __version__
from watchpoint.core import audit  # noqa: F401  (registers the audit table)
from watchpoint.core.config import Settings, get_settings
from watchpoint.core.database import Database
from watchpoint.modules import users

log = logging.getLogger("watchpoint")

# Each module exposes an APIRouter; new modules (alarms, variables, ...) are added here.
MODULES = [users]


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        db: Database = app.state.db
        db.create_all()
        with db.session() as session:
            generated = users.service.bootstrap_admin(session, settings)
        if generated:
            log.warning(
                "Bootstrap administrator %r created with password: %s (change it at first login)",
                settings.bootstrap_admin_login,
                generated,
            )
        yield
        db.engine.dispose()

    app = FastAPI(title="Watchpoint", version=__version__, lifespan=lifespan)
    app.state.db = Database(settings.database_url)
    app.dependency_overrides[get_settings] = lambda: settings

    for module in MODULES:
        app.include_router(module.router, prefix="/api")

    @app.get("/api/health", tags=["system"])
    def health():
        return {"status": "ok", "version": __version__}

    return app


app = create_app()

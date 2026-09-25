import pytest
from fastapi.testclient import TestClient

from watchpoint.core.config import Settings
from watchpoint.main import create_app

ADMIN_PASSWORD = "Admin-Passw0rd!"
STRONG_PASSWORD = "Operator-Passw0rd!"


@pytest.fixture
def settings() -> Settings:
    return Settings(
        database_url="sqlite://",
        secret_key="test-secret-key-that-is-long-enough-for-hs256",
        bootstrap_admin_password=ADMIN_PASSWORD,
        max_failed_logins=3,
    )


@pytest.fixture
def client(settings):
    with TestClient(create_app(settings)) as c:
        yield c


def login(client, login_name, password):
    return client.post("/api/auth/login", json={"login": login_name, "password": password})


def auth(token):
    return {"Authorization": f"Bearer {token}"}


@pytest.fixture
def admin(client):
    """Headers of the bootstrap admin, after the mandatory first password change."""
    token = login(client, "admin", ADMIN_PASSWORD).json()["access_token"]
    r = client.post(
        "/api/auth/me/password",
        json={"current_password": ADMIN_PASSWORD, "new_password": "Supervis0r-Key!"},
        headers=auth(token),
    )
    assert r.status_code == 200, r.text
    return auth(r.json()["access_token"])


@pytest.fixture
def make_user(client, admin):
    def _make(login_name, permissions=(), **extra):
        group_ids = []
        if permissions:
            g = client.post(
                "/api/groups", json={"name": f"{login_name}-group", "permissions": list(permissions)}, headers=admin
            )
            assert g.status_code == 201, g.text
            group_ids = [g.json()["id"]]
        body = {"login": login_name, "password": STRONG_PASSWORD, "must_change_password": False, "group_ids": group_ids}
        r = client.post("/api/users", json={**body, **extra}, headers=admin)
        assert r.status_code == 201, r.text
        token = login(client, login_name, STRONG_PASSWORD).json()["access_token"]
        return r.json(), auth(token)

    return _make

from tests.conftest import ADMIN_PASSWORD, STRONG_PASSWORD, auth, login


def test_bootstrap_admin_must_change_password(client):
    r = login(client, "admin", ADMIN_PASSWORD)
    assert r.status_code == 200
    assert r.json()["must_change_password"] is True
    headers = auth(r.json()["access_token"])

    assert client.get("/api/auth/me", headers=headers).status_code == 200
    assert client.get("/api/users", headers=headers).status_code == 403


def test_login_is_case_insensitive_and_generic_on_failure(client, admin):
    assert login(client, "ADMIN", "Supervis0r-Key!").status_code == 200
    unknown = login(client, "nobody", "whatever")
    wrong = login(client, "admin", "wrong")
    assert unknown.status_code == wrong.status_code == 401
    assert unknown.json() == wrong.json()


def test_me_returns_effective_permissions(client, make_user):
    _, headers = make_user("op1", permissions=["users.read", "audit.read"])
    me = client.get("/api/auth/me", headers=headers).json()
    assert me["login"] == "op1"
    assert me["permissions"] == ["audit.read", "users.read"]


def test_lockout_after_failed_attempts_and_unlock(client, admin, make_user):
    user, _ = make_user("op1")
    for _ in range(3):
        assert login(client, "op1", "bad").status_code == 401
    # Locked: even the right password is refused.
    assert login(client, "op1", STRONG_PASSWORD).status_code == 401
    assert client.get(f"/api/users/{user['id']}", headers=admin).json()["is_locked"] is True

    assert client.post(f"/api/users/{user['id']}/unlock", headers=admin).status_code == 200
    assert login(client, "op1", STRONG_PASSWORD).status_code == 200


def test_disabled_user_cannot_login_and_token_is_revoked(client, admin, make_user):
    user, headers = make_user("op1")
    assert client.get("/api/auth/me", headers=headers).status_code == 200
    assert client.patch(f"/api/users/{user['id']}", json={"is_enabled": False}, headers=admin).status_code == 200
    assert client.get("/api/auth/me", headers=headers).status_code == 401
    assert login(client, "op1", STRONG_PASSWORD).status_code == 401


def test_password_change_revokes_old_tokens(client, make_user):
    _, old = make_user("op1")
    r = client.post(
        "/api/auth/me/password",
        json={"current_password": STRONG_PASSWORD, "new_password": "Another-Passw0rd!"},
        headers=old,
    )
    assert r.status_code == 200
    assert client.get("/api/auth/me", headers=old).status_code == 401
    assert client.get("/api/auth/me", headers=auth(r.json()["access_token"])).status_code == 200


def test_password_policy(client, make_user):
    _, headers = make_user("op1")
    for weak in ["short1!", "alllowercaseletters", "op1-Passw0rd-long"]:
        r = client.post(
            "/api/auth/me/password", json={"current_password": STRONG_PASSWORD, "new_password": weak}, headers=headers
        )
        assert r.status_code == 422, weak


def test_invalid_token(client):
    assert client.get("/api/auth/me").status_code == 401
    assert client.get("/api/auth/me", headers=auth("garbage")).status_code == 401

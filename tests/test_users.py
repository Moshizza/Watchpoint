from tests.conftest import STRONG_PASSWORD


def test_user_crud(client, admin):
    r = client.post(
        "/api/users",
        json={"login": "jdoe", "password": STRONG_PASSWORD, "full_name": "John Doe", "email": "jdoe@example.com"},
        headers=admin,
    )
    assert r.status_code == 201, r.text
    user = r.json()
    assert user["must_change_password"] is True
    assert "password" not in user and "password_hash" not in user

    assert (
        client.post("/api/users", json={"login": "JDOE", "password": STRONG_PASSWORD}, headers=admin).status_code == 409
    )

    r = client.patch(f"/api/users/{user['id']}", json={"full_name": "Jane Doe", "email": None}, headers=admin)
    assert r.json()["full_name"] == "Jane Doe"
    assert r.json()["email"] is None

    logins = [u["login"] for u in client.get("/api/users", headers=admin).json()]
    assert logins == ["admin", "jdoe"]

    assert client.delete(f"/api/users/{user['id']}", headers=admin).status_code == 204
    assert client.get(f"/api/users/{user['id']}", headers=admin).status_code == 404


def test_permissions_are_enforced(client, make_user):
    _, reader = make_user("reader", permissions=["users.read"])
    assert client.get("/api/users", headers=reader).status_code == 200
    assert (
        client.post("/api/users", json={"login": "x", "password": STRONG_PASSWORD}, headers=reader).status_code == 403
    )
    assert client.get("/api/groups", headers=reader).status_code == 403


def test_cannot_delete_or_disable_self_or_last_superuser(client, admin):
    me = client.get("/api/auth/me", headers=admin).json()
    assert client.delete(f"/api/users/{me['id']}", headers=admin).status_code == 403
    assert client.patch(f"/api/users/{me['id']}", json={"is_enabled": False}, headers=admin).status_code == 403
    assert client.patch(f"/api/users/{me['id']}", json={"is_superuser": False}, headers=admin).status_code == 403


def test_manager_cannot_escalate_privileges(client, admin, make_user):
    manager, headers = make_user("manager", permissions=["users.read", "users.manage", "groups.read"])
    admins = client.post("/api/groups", json={"name": "Admins", "permissions": ["groups.manage"]}, headers=admin).json()
    admin_id = client.get("/api/auth/me", headers=admin).json()["id"]

    # Cannot create a superuser nor promote themselves.
    body = {"login": "evil", "password": STRONG_PASSWORD, "is_superuser": True}
    assert client.post("/api/users", json=body, headers=headers).status_code == 403
    assert client.patch(f"/api/users/{manager['id']}", json={"is_superuser": True}, headers=headers).status_code == 403
    # Cannot join a group granting rights they do not hold.
    r = client.patch(f"/api/users/{manager['id']}", json={"group_ids": [admins["id"]]}, headers=headers)
    assert r.status_code == 403
    # Cannot take over a superuser account.
    r = client.post(f"/api/users/{admin_id}/password", json={"new_password": "Hijack-Passw0rd!"}, headers=headers)
    assert r.status_code == 403


def test_admin_password_reset_forces_change(client, admin, make_user):
    user, old = make_user("op1")
    r = client.post(f"/api/users/{user['id']}/password", json={"new_password": "Reset-Passw0rd!"}, headers=admin)
    assert r.status_code == 200
    assert client.get("/api/auth/me", headers=old).status_code == 401
    r = client.post("/api/auth/login", json={"login": "op1", "password": "Reset-Passw0rd!"})
    assert r.json()["must_change_password"] is True


def test_unknown_group_is_rejected(client, admin):
    body = {"login": "op1", "password": STRONG_PASSWORD, "group_ids": [999]}
    assert client.post("/api/users", json=body, headers=admin).status_code == 422

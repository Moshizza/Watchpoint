def test_group_crud_and_membership(client, admin, make_user):
    r = client.post(
        "/api/groups",
        json={"name": "Operators", "description": "Control room", "permissions": ["users.read"]},
        headers=admin,
    )
    assert r.status_code == 201, r.text
    group = r.json()
    assert group["permissions"] == ["users.read"]
    assert client.post("/api/groups", json={"name": "operators"}, headers=admin).status_code == 409

    user, headers = make_user("op1")
    assert client.get("/api/users", headers=headers).status_code == 403
    client.patch(f"/api/users/{user['id']}", json={"group_ids": [group["id"]]}, headers=admin)
    assert client.get("/api/users", headers=headers).status_code == 200

    # Rights changes apply immediately to members.
    r = client.patch(f"/api/groups/{group['id']}", json={"permissions": ["audit.read"]}, headers=admin)
    assert r.json()["permissions"] == ["audit.read"]
    assert client.get("/api/users", headers=headers).status_code == 403
    assert client.get("/api/audit", headers=headers).status_code == 200

    assert client.delete(f"/api/groups/{group['id']}", headers=admin).status_code == 204
    assert client.get(f"/api/users/{user['id']}", headers=admin).json()["groups"] == []


def test_unknown_permission_is_rejected(client, admin):
    r = client.post("/api/groups", json={"name": "X", "permissions": ["nuclear.launch"]}, headers=admin)
    assert r.status_code == 422


def test_group_manager_can_only_grant_own_rights(client, make_user):
    _, headers = make_user("gm", permissions=["groups.read", "groups.manage"])
    ok = client.post("/api/groups", json={"name": "Viewers", "permissions": ["groups.read"]}, headers=headers)
    assert ok.status_code == 201
    r = client.post("/api/groups", json={"name": "Escalate", "permissions": ["users.manage"]}, headers=headers)
    assert r.status_code == 403


def test_permission_registry_is_listed(client, admin):
    codes = {p["code"] for p in client.get("/api/permissions", headers=admin).json()}
    assert {"users.read", "users.manage", "groups.read", "groups.manage", "audit.read"} <= codes


def test_audit_journal_records_actions(client, admin, make_user):
    make_user("op1")
    client.post("/api/auth/login", json={"login": "op1", "password": "wrong"})
    events = client.get("/api/audit", headers=admin).json()
    actions = [e["action"] for e in events]
    assert "user.created" in actions
    assert "auth.login_failed" in actions
    assert "user.bootstrap" in actions
    failed = client.get("/api/audit", params={"action": "auth.login_failed"}, headers=admin).json()
    assert failed[0]["details"] == {"reason": "bad_password"}

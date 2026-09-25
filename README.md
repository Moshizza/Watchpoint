# Watchpoint

Security hypervision platform (PSIM), in the spirit of PRYSM AppVision: one place to
supervise and control access control, intrusion, video, fire and technical systems.

The platform is built as independent **modules** on top of a small **core**:

```
watchpoint/
  core/            config, database, security (hashing/JWT), permission registry, audit journal
  modules/
    users/         operator accounts, groups (profiles), rights, authentication
  main.py          application factory; MODULES lists the modules mounted under /api
```

## Modules

### Users (`watchpoint.modules.users`)

| Concept | Description |
|---|---|
| **User** | Operator account: login, name, contact, language, enabled/locked state. |
| **Group** | A profile ("Operators", "Supervisors", ...) holding a set of rights. Users may belong to several groups; their rights are the union. |
| **Permission** | A code such as `users.manage`. Every module registers its codes in `core.permissions`, so group editors can list them from `GET /api/permissions`. |
| **Superuser** | Bypasses all permission checks. The last enabled superuser cannot be deleted, disabled or demoted. |
| **Audit journal** | Logins, failed logins, lockouts and every change to users/groups are recorded with actor, time and client IP. |

Security rules:

- Passwords hashed with Argon2; policy: minimum length, 3 character classes, must not contain the login.
- Account lockout after `max_failed_logins` failed attempts for `lockout_minutes`.
- Login failures return the same error whatever the cause (no login enumeration).
- Tokens are revoked when the password changes, is reset, or the account is disabled.
- `must_change_password` accounts can only reach `/api/auth/me` until they change it.
- No privilege escalation: a non-superuser can only grant rights they hold themselves, and cannot modify superuser accounts.

API (all under `/api`, interactive docs at `/docs`):

| Method | Path | Permission |
|---|---|---|
| POST | `/auth/login` | — |
| GET | `/auth/me` | authenticated |
| POST | `/auth/me/password` | authenticated |
| GET | `/users`, `/users/{id}` | `users.read` |
| POST/PATCH/DELETE | `/users`, `/users/{id}` | `users.manage` |
| POST | `/users/{id}/password`, `/users/{id}/unlock` | `users.manage` |
| GET | `/groups`, `/groups/{id}` | `groups.read` |
| POST/PATCH/DELETE | `/groups`, `/groups/{id}` | `groups.manage` |
| GET | `/permissions` | authenticated |
| GET | `/audit` | `audit.read` |

## Running

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e ".[dev]"
export WATCHPOINT_SECRET_KEY=$(python -c "import secrets; print(secrets.token_urlsafe(48))")
uvicorn watchpoint.main:app --reload
```

On first start an `admin` superuser is created. Its password comes from
`WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD`, or is generated and printed once in the log;
it must be changed at first login.

Settings (environment variables, or a `.env` file):

| Variable | Default |
|---|---|
| `WATCHPOINT_DATABASE_URL` | `sqlite:///./watchpoint.db` |
| `WATCHPOINT_SECRET_KEY` | random per start (dev only) |
| `WATCHPOINT_ACCESS_TOKEN_MINUTES` | `480` |
| `WATCHPOINT_MAX_FAILED_LOGINS` | `5` |
| `WATCHPOINT_LOCKOUT_MINUTES` | `15` |
| `WATCHPOINT_PASSWORD_MIN_LENGTH` | `10` |

## Tests

```bash
pytest
ruff check .
```

## Adding a module

1. Create `watchpoint/modules/<name>/` with its models, `permissions.py` (calls `core.permissions.register`) and an `APIRouter` exported as `router`.
2. Protect routes with `Depends(require_permission("<name>.<action>"))` from `watchpoint.modules.users.deps`.
3. Record operator actions with `watchpoint.core.audit.record(...)`.
4. Add the module to `MODULES` in `watchpoint/main.py`.

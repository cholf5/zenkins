# Completion notifications (outbound webhooks)

A project can notify one or more endpoints when a run ends. The delivery is a signed JSON `POST`, owed the moment
the run reaches its terminal state and delivered afterwards from a durable outbox - a dead receiver never delays a
build, and a Server restart never loses a notification.

This page is the receiver's contract. It is the counterpart of [`docs/api-v1.md`](api-v1.md) (the pull API) and of
the inbound webhook in PRD §3.9.

## Events

| Event | When |
|---|---|
| `failed` | The run ended `Failed` (a non-zero exit, or a timeout reported by the worker). |
| `success` | The run ended `Success` and the previous terminal run of the same project **and branch** did not fail. |
| `recovered` | The run ended `Success` and the previous terminal run of the same project and branch ended `Failed` (or `Lost`). |

Three notes that matter when you wire a chat bridge:

- **`recovered` is its own event, not a flag on `success`.** It is the one people actually watch: "back to green".
- **A `success` subscription also matches `recovered`.** A recovery *is* a success; a bridge that asked for successes
  and dropped the recovery would tell the wrong story at exactly the moment people look. A `recovered` subscription
  matches recoveries only, and `all` matches everything.
- **Canceled and lost runs do not notify.** Their "result" is not a result to announce. A `Lost` run still counts as
  a failure for the **next** run's recovery decision.

The subscriptions are configured per endpoint, so one project can send `failed` to a pager and `all` to an audit log.

## The request

```
POST /your/receiver/path
Content-Type: application/json; charset=utf-8
X-Zenkins-Event: failed | success | recovered | test
X-Zenkins-Delivery: 1f0b2c34-5d6e-4f70-8a9b-0c1d2e3f4a5b
X-Zenkins-Attempt: 1
X-Zenkins-Signature: sha256=3f1c...e9
```

| Header | Meaning |
|---|---|
| `X-Zenkins-Event` | The event, so a receiver can route without parsing the body. |
| `X-Zenkins-Delivery` | The delivery's **stable** id: a retry repeats it, a new terminal state gets a new one. Deduplicate on this. |
| `X-Zenkins-Attempt` | `1` for the first send, `2` for the first retry, … |
| `X-Zenkins-Signature` | `sha256=` + the lower-case hex HMAC-SHA256 of the **raw body bytes** with the endpoint's shared secret. Same scheme as the inbound webhook (PRD §3.9). |

A `test` delivery (the settings card's "send test" button) has `X-Zenkins-Event: test` and describes the project's
newest run. It is never produced by a run.

## The payload

camelCase, one object, no nesting beyond the two records below:

```json
{
  "event": "recovered",
  "deliveryId": "1f0b2c34-5d6e-4f70-8a9b-0c1d2e3f4a5b",
  "attempt": 1,
  "run": {
    "id": "9c1f7c1e-2a5b-4c3d-8e9f-0a1b2c3d4e5f",
    "number": 12,
    "branch": "main",
    "commit": "3f1c9a2…",
    "status": "Success",
    "triggerType": "Manual",
    "url": "https://zenkins.example.test/runs/9c1f7c1e-2a5b-4c3d-8e9f-0a1b2c3d4e5f",
    "rebuiltFromRunId": null,
    "rebuiltFromRunNumber": null
  },
  "project": {
    "id": "0b7c…",
    "name": "game-server",
    "url": "https://zenkins.example.test/projects/0b7c…"
  },
  "failureReason": null,
  "finishedAt": "2026-11-19T08:41:12.0000000+00:00",
  "sentAt": "2026-11-19T08:41:13.5120000+00:00"
}
```

| Field | Meaning |
|---|---|
| `run.number` | The build number inside the project (`#12`), see PRD §3.5. |
| `run.commit` | The commit the run built (null for a Freestyle run). |
| `run.triggerType` | `Manual`, `Webhook`, `Api`, `Rebuild`, `Replay`, `Upstream`, `Schedule` or `Poll`. |
| `run.url` | Absolute when `Notifications:PublicBaseUrl` is configured, otherwise the relative path `/runs/{id}`. |
| `run.rebuiltFromRunId` / `Number` | Set when this run rebuilt or replayed another one (PRD §3.5's re-runs). |
| `failureReason` | The worker's closed failure reason, or null. |
| `finishedAt` | When the run reached its terminal state. |
| `sentAt` | When *this attempt* was made (a retry moves it; `finishedAt` does not). |

**What is deliberately absent:** build parameters (a value can be a password, PRD §3.11), the organization
environment, secrets, credentials, log text and artifact contents. The payload is the run's identity and its
outcome - if a notification needs more, fetch it from [`docs/api-v1.md`](api-v1.md) with a token.

## Verifying the signature

Sign the **raw bytes** you received, not a re-serialised object: key order and escaping differ between
serializers, and re-serialising is the classic way to make a receiver look broken against a real sender.

Python (standard library only):

```python
import hashlib, hmac

def is_from_zenkins(secret: str, body: bytes, header: str | None) -> bool:
    if not header:
        return False
    expected = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, header.strip().lower())
```

Node:

```js
const { createHmac, timingSafeEqual } = require("node:crypto");

function isFromZenkins(secret, body, header) {
  const expected = "sha256=" + createHmac("sha256", secret).update(body).digest("hex");
  const a = Buffer.from(expected);
  const b = Buffer.from((header || "").trim().toLowerCase());
  return a.length === b.length && timingSafeEqual(a, b);
}
```

Compare in constant time and answer `2xx` only after the signature checks out. A `2xx` is what settles the
delivery; anything else is a failed attempt.

## Delivery, retries and what the page shows

- **At-least-once.** The obligation is a row in the same outbox PRD §3.14/§3.5 already uses for chain triggers,
  written in the same transaction as the terminal status. A delivery is retried until it succeeds or the attempt
  bound is spent, so a receiver can see the same `X-Zenkins-Delivery` twice - deduplicate on it.
- **Bounded.** `Notifications:MaxAttempts` (default 5) with a linear backoff of `Notifications:BackoffSeconds`
  (default 5s, × the attempt) per delivery. After the bound the delivery is recorded as `failed` with the last
  error, and nothing retries it again.
- **Timeout.** `Notifications:TimeoutSeconds` (default 10) per request. A receiver that accepts the connection and
  then says nothing is a failed attempt, not a hung dispatcher.
- **No redirects.** A `3xx` is a failed attempt; the `Location` is never followed. Plain `http` endpoints need an
  explicit per-endpoint opt-in (the settings card shows the warning).
- **The response body is read up to 4 KiB** and only used for the recorded error - "receiver said: …" on the
  settings card.
- **The settings card shows, per endpoint:** enabled/disabled, the subscribed events, the last delivery time, the
  last outcome (`delivered`, `retrying`, `failed`) and the last error (truncated). "Never delivered" means the
  endpoint has not been attempted yet.
- **Rotation.** Replacing an endpoint's secret changes what *future* deliveries are signed with. A delivery already
  owed keeps the secret it was created under, so a receiver that has not been updated yet can still verify what is
  in flight. Update the receiver, then rotate.
- **A disabled project still notifies** about the runs it already had: PRD §3.5's brake stops new triggers, not the
  history they produced.

## Configuration (Server)

| Key | Default | Meaning |
|---|---|---|
| `Notifications:TimeoutSeconds` | `10` | One request's budget. |
| `Notifications:MaxAttempts` | `5` | Attempts before a delivery settles as `failed`. |
| `Notifications:BackoffSeconds` | `5` | Base of the linear backoff between attempts (`0` retries immediately). |
| `Notifications:MaxResponseBytes` | `4096` | How much of the receiver's answer is read. |
| `Notifications:PublicBaseUrl` | unset | The Server's public base URL, used for `run.url` / `project.url`. Unset means the relative path. |
| `Notifications:PollIntervalSeconds` | `2` | How often the dispatcher looks for due deliveries. |

# Zapqio Runner Protocol — v1 (Draft)

This is the **source of truth** for the wire protocol between the Zapqio **Web** server and a
**Runner**. Any runner — in any language — that conforms to this document can connect to Web.

This version is *descriptive*: it documents the behaviour of the reference .NET implementation as of
2026-06-12. The reference binding lives in `Zapqio.Protocol`; §11
maps every rule below to the code that implements it, so the spec can be re-verified.

The key words MUST, MUST NOT, SHOULD and MAY are used in the RFC 2119 sense.

---

## 1. Overview

A **Runner** is a WebSocket **client**. **Web** is the WebSocket **server**. The runner connects,
authenticates with HTTP headers, announces the methods it can execute, then receives jobs, executes
them, streams logs, and returns results. Every application message is a JSON text frame sharing a
common [envelope](#4-envelope).

Web is **agnostic** to how a runner executes a job or what language it is written in. It routes jobs
by `(runner, method name)` and treats each job's input and output as an **opaque string**. Runners
may therefore be heterogeneous: a single pipeline can have steps executed by different runners in
different languages.

A complete runner implementation is three layers, but **only the first crosses the language
boundary**:

1. **The protocol** — this document. (Reimplement in your language.)
2. **The host** — the connection lifecycle, the job loop, the log queue, dispatch-by-method-name.
3. **A module model** — how *your* language defines and executes methods. This is yours to design
   (e.g. decorators + a JSON-Schema generator). The .NET `IRunnerMethod` / assembly-loading model
   does **not** travel; modules are per-language.

```
 Runner (client)                                  Web (server)
      |   ── WebSocket upgrade: GET /ws-runner ──────>   | 101 (or 401/400)
      |        headers: X-Zapqio-Token, X-Zapqio-Name    |
      |                                                   |
      |   ── Info (methods + name) ───────────────────>  |  (stores methods)
      |                                                   |
      |   <─────────────────────── Job (dispatch) ─────  |  (server pushes work)
      |   ── Log … Log … ─────────────────────────────>  |  (streamed while running)
      |   ── JobReturn (OK/ERROR + output) ───────────>  |  (stores result, pipes to next step)
      |   ── Job (poll, data=null) ───────────────────>  |  ("give me more")
      |                                                   |
```

---

## 2. Transport & framing

- **WebSocket** (RFC 6455). Endpoint: `GET {baseUrl}/ws-runner` with the standard upgrade, where
  `{baseUrl}` is the Web origin (e.g. `wss://zapqio.example.com`).
- Application messages are **text** frames, **UTF-8**, each a single JSON object (the envelope).
  Implementations MUST reassemble continuation frames until FIN before parsing.
- Server maximum message size: **32 MiB** (33 554 432 bytes). A larger message is closed with WS
  status **1009** (Message Too Big).
- No application-level compression or batching.

---

## 3. Handshake & authentication

On the upgrade request the runner MUST send two headers:

| Header | Meaning |
| --- | --- |
| `X-Zapqio-Token` | Secret runner token, in plaintext. Web verifies it against stored Argon2 hashes. |
| `X-Zapqio-Name`  | The runner's stable, self-assigned name (from its config/env). |
| `X-Zapqio-Protocol-Version` | The protocol **major version** the runner speaks (e.g. `1`). Optional for now; a missing header is treated as `1`. |

Server behaviour:

- Request to `/ws-runner` that is **not** a WebSocket upgrade → HTTP **400**.
- Either header missing/empty → HTTP **401**.
- Token matching **no** runner → HTTP **401**.
- **Name binding:** the first time a runner connects, Web binds `X-Zapqio-Name` to that runner
  record. On later connects the name MUST equal the bound name, otherwise → HTTP **401**.
- Unsupported protocol version → HTTP **426 Upgrade Required** (response carries
  `X-Zapqio-Protocol-Version: <server version>`); a non-integer version → HTTP **400**.
- Otherwise → **101 Switching Protocols**.

Notes:

- The **token is the identity & secret**; the **name is a stable label**. Pick a name once and keep
  it stable. (The reference runner reads `ZAPQIO_NAME`, or generates a UUID and persists it to a
  `##Name` file if none is configured.)
- **Protocol version** is negotiated by the `X-Zapqio-Protocol-Version` header (an integer major
  version). A **missing** header → assumed `1` (the pre-versioning baseline); a **non-integer** →
  HTTP 400; a value the server does **not** support → **426 Upgrade Required**, with the server's
  version echoed in an `X-Zapqio-Protocol-Version` response header. The major version is bumped only
  on a breaking change (§9).

---

## 4. Envelope

Every WebSocket message is exactly this object:

```json
{ "type": "Job", "data": "…" }
```

- **`type`** *(string, required)* — the message type: one of `Info`, `Job`, `JobReturn`, `Log`.
  Exact casing in §6.
- **`data`** *(string or null, required)* — the payload for that type, **JSON-encoded as a string**.
  The payload object is serialized to JSON and that JSON *text* is placed in `data` as a string
  value. `null` only for the [Job poll](#52-job).

> **⚠ GOTCHA — double encoding.** `data` is a **string**, not a nested object. To **read** a
> message: parse the envelope, then parse `data` *again* as JSON. To **write** one: serialize the
> payload to a string and assign it to `data`. Worked bytes in §8.

All object property names are **camelCase** (`type`, `data`, `id`, `name`, `jobId`, `level`, …).

---

## 5. Messages & flow

Direction legend: **R→W** runner→web, **W→R** web→runner.

### 5.1 Info (R→W)

Sent **once**, right after the first successful connect. Announces the runner's name and the methods
it exposes. Payload — `MessageInfo`:

```json
{
  "name": "build-agent-01",
  "methods": [
    { "name": "resize-image", "in": "{\"type\":\"object\", … }", "out": "{\"type\":\"object\", … }" }
  ]
}
```

- `name` — runner name (same value as `X-Zapqio-Name`).
- `methods` — array of `MessageMethod`:
  - `name` — method name; jobs are routed to it by this name.
  - `in`  — a **JSON Schema** describing the method **input**, carried *as a string*, or `null`.
  - `out` — a **JSON Schema** describing the method **output**, carried *as a string*, or `null`.

Web stores the method list; the UI uses `in`/`out` to render and validate pipeline job I/O. The
reference runner sends `Info` once per process; **re-send `Info` if your method set changes** (e.g.
after a reconnect with a different module set).

### 5.2 Job

`Job` is **overloaded by direction**:

- **Poll (R→W):** the envelope `{ "type": "Job", "data": null }`. Means *"I am free, send me work."*
  If Web has nothing to dispatch it sends nothing back.
- **Dispatch (W→R):** payload — `MessageJob`:

  ```json
  { "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890", "name": "resize-image", "data": "{\"width\":800}" }
  ```

  - `id`   — the job's unique id; echo it in `Log` and `JobReturn`.
  - `name` — which method to run (matches a `MessageMethod.name`).
  - `data` — the job **input**: itself a JSON string (matching the method's `in` schema), possibly
    empty.

> **⚠ GOTCHA — triple nesting.** `envelope.data` is a string containing `MessageJob` JSON, and
> `MessageJob.data` is *itself* a string containing the job-input JSON. Two levels of
> string-encoding on top of the frame.

**Kickstart & cadence.** After `Info`, the runner blocks on receive; the **first** job arrives from
Web's background dispatcher (~every 10 s). The runner executes **one job at a time** and, after each
job finishes, sends a **Job poll** to pull the next. A dispatch that arrives while a job is already
running MAY be ignored by the runner (the reference runner drops it).

### 5.3 Log (R→W)

Streamed while a job runs. Payload — `MessageLog`:

```json
{
  "jobId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "level": "Info",
  "message": "Run Job: 2026-06-12T14:30:00",
  "date": "2026-06-12T14:30:00.123+00:00"
}
```

- `jobId`   — the job this log line belongs to.
- `level`   — `Info` or `Error` (§6).
- `message` — log text.
- `date`    — ISO-8601 timestamp with timezone offset (§7).

The **first** `Log` for a job flips it from *Waiting* to *Executing* on the server. The reference
runner captures the method's `stdout`→`Info` and `stderr`→`Error`, plus a start line and any
exception; entries are flushed on a ~2 s timer, **one WS message per entry**.

### 5.4 JobReturn (R→W)

Sent **once** when a job finishes. Payload — `MessageJobReturn`:

```json
{ "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890", "status": "OK", "data": "{\"url\":\"…\"}" }
```

- `id`     — the job id.
- `status` — `OK` or `ERROR` (§6).
- `data`   — on `OK`: the job **output** (a JSON string matching `out`), which Web feeds as the
  **input of the next pipeline step**. On `ERROR`: `null`.

### 5.5 Typical exchange (one job)

```
W→R  Job        { id: J, name: "resize-image", data: "{…input…}" }
R→W  Log        { jobId: J, level: Info,  message: "Run Job: …" }
R→W  Log        { jobId: J, level: Info,  message: "…method output…" }
R→W  JobReturn  { id: J, status: OK, data: "{…output…}" }
R→W  Job        null                              # poll for the next job
```

---

## 6. Enumerations (exact wire values)

Enums are serialized as their **exact names** — the reference uses a string enum converter with **no
naming policy**, so values are **PascalCase / uppercase, NOT camelCase**. Producers MUST emit
exactly these strings:

| Field | Allowed values |
| --- | --- |
| message `type`     | `Info`, `Job`, `JobReturn`, `Log` |
| Log `level`        | `Info`, `Error` |
| JobReturn `status` | `OK`, `ERROR` |

The reference .NET *deserializer* happens to accept other casing and integer values on read, but a
conformant producer MUST emit exactly the strings above and MUST NOT emit integers.

---

## 7. Scalar formats

| Logical type | Wire format | Example |
| --- | --- | --- |
| uuid (`id`, `jobId`) | canonical hyphenated, lowercase UUID string | `"a1b2c3d4-e5f6-7890-abcd-ef1234567890"` |
| timestamp (`date`)   | ISO-8601 with timezone offset; fractional seconds optional | `"2026-06-12T14:30:00.123+00:00"` |
| JSON Schema (`in`, `out`) | a JSON Schema document carried **as a string** | `"{\"type\":\"object\", … }"` |
| job input/output (`data` of `MessageJob`/`MessageJobReturn`) | opaque JSON carried **as a string**; shape defined per method by `in`/`out`, not by this protocol | `"{\"width\":800}"` |

---

## 8. Worked example — bytes on the wire

A **Job dispatch** for method `resize-image` with input `{"width":800,"height":600}`. The exact
text frame Web sends:

```
{"type":"Job","data":"{\"id\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\",\"name\":\"resize-image\",\"data\":\"{\\\"width\\\":800,\\\"height\\\":600}\"}"}
```

Decoding it, level by level:

1. **Frame → envelope:** `{ "type": "Job", "data": "{\"id\":\"a1b2…\",\"name\":\"resize-image\",\"data\":\"{\\\"width\\\":800,…}\"}" }`
2. **Parse `data` → `MessageJob`:** `{ "id": "a1b2…", "name": "resize-image", "data": "{\"width\":800,\"height\":600}" }`
3. **Parse `MessageJob.data` → input:** `{ "width": 800, "height": 600 }`

Every fixture in [`fixtures/`](./fixtures/) is one such exact frame.

---

## 9. Versioning & compatibility

- This is **v1**, descriptive of the current reference implementation.
- **Version negotiation** happens on the handshake via the `X-Zapqio-Protocol-Version` header (§3):
  the runner sends its major version; the server accepts only versions it supports, replying **426
  Upgrade Required** otherwise. A missing header is treated as `1` for backward compatibility with
  pre-versioning runners.
- Compatibility rules for future revisions: adding an **optional** field is backward compatible;
  removing/renaming a field, or changing an enum string, is **breaking** and requires a version bump.

---

## 10. Conformance

An implementation conforms if it can both **produce** and **consume** every fixture in
[`fixtures/`](./fixtures/) such that, after full decoding (frame → envelope → payload → nested job
I/O), the logical content equals the fixture's documented content.

Comparison is **semantic** (parsed structures are deep-equal), **not byte-exact**: insignificant
whitespace and object key order do not matter. Producers SHOULD nonetheless emit the field casing
and enum strings exactly as specified, because not every consumer is lenient.

---

## 11. Reference implementation map

This document, with [`schemas.json`](./schemas.json) and [`fixtures/`](./fixtures/), is *intended* as
the source of truth: the .NET code here is **one** implementation of it, not its definition.

Be aware of the current gap — no automated suite yet checks the .NET code against the fixtures, so in
practice the code can drift from this document without anything failing. Until that suite exists,
treat any disagreement between code and spec as a bug worth reporting rather than as settled.

The reference runner is a WebSocket **client**. Its layout, for readers who want to see a rule in
working code:

| Concern | File |
| --- | --- |
| Envelope + JSON options (camelCase, string enums) | `Zapqio.Protocol/Message.cs`, `Zapqio.Protocol/JsonDefaults.cs` |
| Payload shapes | `Zapqio.Protocol/Message{Info,Method,Job,JobReturn,Log}.cs` |
| Enum definitions | `Zapqio.Protocol/Enums/Message{Type,LogLevel,ResponseStatus}.cs` |
| Negotiated version constant | `Zapqio.Protocol/ProtocolVersion.cs` |
| Handshake & sending (client) | `Zapqio.Runner/WSClient.cs` |
| Runner loop (Info, poll, dispatch) | `Zapqio.Runner/Background/RequestBindBackground.cs` |
| Log queue & flush cadence | `Zapqio.Runner/Background/SendLogsBackground.cs`, `Zapqio.Runner/LogQueue.cs`, `Zapqio.Runner/ScopedConsole.cs` |

The **server** side (Web) is not part of this repository. Everything a runner needs to interoperate
with it is specified here: the handshake and its rejection codes (§3), the envelope (§4), the
message set and their direction (§5), and the version negotiation rules (§9). No behaviour of the
server beyond this document may be relied upon.

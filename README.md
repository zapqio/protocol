# Zapqio Runner Protocol

This directory is the **language-neutral source of truth** for the wire protocol between the
Zapqio **Web** server and a **Runner**. A runner written in *any* language can connect to Web by
conforming to it.

| File | Purpose |
| --- | --- |
| [`PROTOCOL.md`](./PROTOCOL.md) | The normative specification — **read this first**. |
| [`PROTOCOL.pl.md`](./PROTOCOL.pl.md) | Polish translation of the specification. Non-normative — `PROTOCOL.md` wins on any disagreement. |
| [`schemas.json`](./schemas.json) | JSON Schema (Draft 2020-12) for the envelope and all payloads. |
| [`fixtures/`](./fixtures/) | Canonical example messages used as cross-language conformance vectors. |

**Status: Draft v1** — descriptive, reverse-engineered from the reference .NET implementation
(`Zapqio.Protocol`). The protocol version is negotiated on the handshake (§3);
see [`PROTOCOL.md` §9](./PROTOCOL.md#9-versioning--compatibility).

The protocol is plain JSON over a WebSocket, so it is implementable in any language. Note that a
*runner* is more than the protocol: it is also a **host** (the connection/poll/log loop) plus a
**per-language module model** (how methods are defined and executed). Only the protocol crosses the
language boundary — see [`PROTOCOL.md` §1](./PROTOCOL.md#1-overview).

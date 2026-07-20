# Conformance fixtures

Each `*.json` file here is **one exact WebSocket text frame** — the literal bytes a conformant
implementation sends or accepts. Because the envelope's `data` field is a JSON string (see
[`../PROTOCOL.md` §4](../PROTOCOL.md#4-envelope)), the encoding is visible as escaped quotes inside
`data` — that is intentional, it is what the gotchas look like in practice.

A cross-language conformance test SHOULD, for every fixture:

1. **Consume:** parse the frame, then parse `data`, then (for jobs/results) parse the nested job
   I/O, and check the fully-decoded content equals the **Decoded** column below.
2. **Produce:** build the same logical message and check it re-encodes to a frame that is
   **semantically equal** to the fixture (deep-equal after decoding — whitespace and key order are
   not significant; see [`../PROTOCOL.md` §10](../PROTOCOL.md#10-conformance)).

All fixtures share one coherent example: runner `build-agent-01`, method `resize-image`, job
`a1b2c3d4-e5f6-7890-abcd-ef1234567890`.

| Fixture | Dir | Decoded meaning |
| --- | --- | --- |
| `info.json` | R→W | Runner announces name `build-agent-01` and one method `resize-image` with an input JSON Schema and `out: null`. |
| `job-poll.json` | R→W | Poll: `type=Job`, `data=null` ("send me work"). |
| `job-dispatch.json` | W→R | Dispatch job `a1b2…` → method `resize-image`, input `{ "width": 800, "height": 600 }`. |
| `log-info.json` | R→W | Info log for job `a1b2…`: `"Run Job: 2026-06-12T14:30:00"`. |
| `log-error.json` | R→W | Error log for job `a1b2…`: `"Main exception: boom"`. |
| `job-return-ok.json` | R→W | Result for job `a1b2…`: `OK`, output `{ "url": "https://cdn.example.com/out/123.png" }`. |
| `job-return-error.json` | R→W | Result for job `a1b2…`: `ERROR`, `data=null`. |

Decoding `job-dispatch.json` step by step is worked through in
[`../PROTOCOL.md` §8](../PROTOCOL.md#8-worked-example--bytes-on-the-wire).

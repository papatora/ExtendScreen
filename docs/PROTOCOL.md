# AzurateMirror wire protocol v1

Single TCP connection, fully multiplexed (no separate control socket). Works identically whether
the connection came in over LAN (direct IP) or over USB (`adb reverse tcp:<port> tcp:<port>`,
client connects to `127.0.0.1:<port>`).

`Protocol.cs` (Windows) and `Protocol.java` (Android) are two independent hand-written
implementations of this spec — C# and Java can't share compiled types across platforms.
**Rule: if you change anything in this file, update both `Protocol.cs` and `Protocol.java` in the
same pass.** There is no runtime schema negotiation; both sides just assume this document.

## Frame header

Every message, either direction, starts with the same 13-byte header:

| Offset | Size | Field       | Notes                                             |
|--------|------|-------------|----------------------------------------------------|
| 0      | 1    | type        | uint8, see Message types below                     |
| 1      | 4    | length      | uint32 big-endian, payload byte count (can be 0)    |
| 5      | 8    | timestamp   | uint64 big-endian, sender's monotonic ms since session start |
| 13     | N    | payload     | `length` bytes, meaning depends on `type`           |

Big-endian was picked so the header is trivially readable in a hex dump without worrying about
endianness per-field.

## Message types

| Type   | Name           | Direction       | Payload                                                                 |
|--------|----------------|-----------------|--------------------------------------------------------------------------|
| `0x01` | HELLO          | client -> server | `protocolVersion(u8=1) + mode(u8: 0=mirror,1=extend) + deviceName(UTF-8 rest of payload)` |
| `0x02` | HELLO_ACK      | server -> client | `accepted(u8 bool) + sessionId(u32 BE) + reason(UTF-8, only if rejected)` |
| `0x03` | VIDEO_CONFIG   | server -> client | `width(u32 BE) + height(u32 BE) + fpsTarget(u8) + codecId(u8=1 for H264) + spsLen(u16 BE) + sps(Annex-B) + ppsLen(u16 BE) + pps(Annex-B)` |
| `0x04` | VIDEO_FRAME    | server -> client | `keyframe(u8 bool) + accessUnit(Annex-B H.264 NAL(s), rest of payload)` |
| `0x05` | CONTROL        | both            | small UTF-8 JSON, e.g. `{"cmd":"stop"}` / `{"cmd":"pause"}` / `{"cmd":"resume"}` / `{"cmd":"refresh"}` / `{"cmd":"request_keyframe"}` |
| `0x06` | STATS_CLIENT   | client -> server | UTF-8 JSON, e.g. `{"decodeFps":29.4,"renderLatencyMs":38,"framesDropped":0}`, pushed ~1x/sec |
| `0x07` | PING           | both            | empty                                                                     |
| `0x08` | PONG           | both            | empty (echo of the PING that triggered it)                              |

## Capture modes (chosen by the Android client, sent in HELLO)

- **Extend (default)** - the Windows sender shows a normal titled, resizable "Canvas" window
  (`CanvasWindow.xaml`) the user drags other app windows into; only that window's screen
  rectangle is captured/encoded/sent. Functions as an app-level "extra screen" without any
  virtual-monitor driver (see the plan's explicit v1 scope decision). The canvas is a bounded
  rectangle on the *same* physical display, not a second GPU output.
- **Mirror** - the full primary display is captured and sent, unscaled (what the pipeline was
  proven against first; see `docs/proof_of_pipeline_2026-08-14.png`).

The server recreates its `FrameEncoder` (new SPS/PPS, fresh `VIDEO_CONFIG`) whenever the active
capture region's pixel size changes - on a mode switch, or when the user resizes the Canvas
window while in Extend mode.

## Behavioral notes

- **VIDEO_CONFIG is re-sent** whenever resolution changes or a `CONTROL{refresh}` happens. The
  Android decoder must fully reconfigure (`stop()`/`release()`/rebuild `MediaFormat`) when it
  receives a new one, not just on first connect.
- **H.264 is carried as raw Annex-B** (start-code-prefixed NAL units), matching exactly what
  `MediaCodec` expects for `csd-0`/`csd-1` (from VIDEO_CONFIG's sps/pps) and for queued input
  buffers (VIDEO_FRAME payload) — no length-prefixed AVC repackaging needed on either side.
- **Stop vs Pause vs Refresh** are distinguished at the CONTROL layer, not by tearing the TCP
  connection down:
  - `stop` — server does full teardown (releases capture/encoder) and closes the socket. Listener
    stays up for a new connection. Client should NOT auto-reconnect after a graceful stop.
  - `pause` — socket and listener stay alive, server just stops pulling/encoding frames. Client
    shows a "Paused" overlay instead of freezing on the last frame, keeps its decoder alive for a
    fast resume.
  - `refresh` — server recreates its capture+encoder objects (may produce new SPS/PPS), sends a
    fresh VIDEO_CONFIG, keeps the TCP connection open. Client reconfigures its decoder in place —
    no reconnect, no app restart on either side.
- **Latency numbers are relative, not lab-grade.** There is no NTP/clock sync between the PC and
  the tablet, so `timestamp` deltas across the two devices are an approximation good enough for a
  live "is it fast right now" HUD reading — do not present them as precise round-trip latency.
- **PING/PONG** run on a fixed interval (proposed: every 3s) so a dead link is detected well before
  a TCP-level timeout would notice — on 2 missed PONGs in a row, the client treats the connection
  as dead and starts its reconnect backoff (see below), even if the socket hasn't errored yet.
- **Reconnect** (client-side only, since the server is a passive listener): exponential backoff
  1s -> 2s -> 4s -> 8s, capped at 10s, only while the "auto-reconnect" setting is on and the last
  disconnect wasn't a graceful `CONTROL{stop}`.

## Session lifecycle (happy path)

```
client -> server : HELLO
server -> client : HELLO_ACK (accepted)
server -> client : VIDEO_CONFIG
server -> client : VIDEO_FRAME (keyframe=1)
server -> client : VIDEO_FRAME (keyframe=0)  ... repeated
client -> server : STATS_CLIENT              ... ~1/sec, interleaved
both              : PING / PONG              ... periodic, interleaved
...
client or server  : CONTROL{stop}            -> socket closes
```

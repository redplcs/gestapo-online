# PRUDP in Commandos: Strike Force

This is the transport protocol between the Commandos: Strike Force client and the Rendez-Vous servers (Eidos Online, now shut down), as implemented in the PC client. It is written for people implementing a compatible server.

**Source.** `CommXPC.exe` (PE timestamp 2006-03-30, image base `0x400000`). The client statically links Quazal Net-Z (© 1998–2004) and the Quazal Rendez-Vous client. Addresses such as `0x0067a960` are functions in this binary.

**How it was checked.** Every statement comes from the decompiled and disassembled code. Unless marked *(inferred)*, it was also confirmed by running the game's own functions under Unicorn (x86 emulation).

Verification ran in three rounds:
1. Analysts compared the game's code with Python reimplementations.
2. Independent verifiers repeated those checks with their own harnesses.
3. A server model written from scratch, using only the findings, was run against the game's client code. It passed all 21 scenarios: handshake, reliable data both ways, fragmentation, keep-alive, the full login and account creation.

**Tested with the real game (2026-10-22).** A test server built from this document was run against the unmodified game on the same machine, with `cxlivePC.quazal.net` pointed at `127.0.0.1` in `hosts`. Account creation, login with the new account and the multiplayer lobby worked, with no dropped packets. Hosting and joining games, NAT traversal and behaviour over the internet have not been tested yet.

The layers above PRUDP are in [rendezvous.md](rendezvous.md): RMC messages, Rendez-Vous protocols and the login sequence.

## 1. Overview

| Layer | Classes in the binary | What it does |
|---|---|---|
| UDP | `QueuingSocket`, `UDPTransport` | One socket per local port. Routes packets to streams by virtual port. |
| PRUDP | `PRUDPTransport`, `PRUDPEndPoint` | Connections, reliability, keep-alive. |
| prudps | `SecureTransport`, `SecureEndPoint` | PRUDP plus a Kerberos-style ticket in the CONNECT packet. |
| RMC | `ProtocolRequestBroker` | Remote method calls carried in DATA payloads. |

The Rendez-Vous client runs one PRUDP transport on **stream type 3**. Both the authentication connection (`prudp`) and the secure connection (`prudps`) use it.

| Parameter | Value |
|---|---|
| Server host / port | `RENDEZVOUS_URL` / `RENDEZVOUS_PORT` from `Config\JuegoVer.cfg`: `cxlivePC.quazal.net` / `30065` |
| Access key | `RENDEZVOUS_PASSWORD` = `7fas5`. Its only use is the checksum seed `0xA6`. |
| Client UDP port | `9103` (falls back to an OS-assigned port if the bind fails) |
| Virtual ports | client `0x3F`, server `0x31` |
| Max datagram | 1000 bytes in both directions |
| Max DATA payload per packet | The client splits at 974 bytes. It accepts up to 987 stored bytes per packet. |
| Reliable window | 8 packets |
| Max transmissions per packet | 20, counting the first one |
| Initial RTO | 250 ms, linear back-off, no lower bound afterwards |
| Keep-alive / silence timeout | 10 000 ms / 60 000 ms |
| Connect timeout | 5 000 ms per URL |

## 2. Addressing

### 2.1 Virtual ports (vports)

Bytes 0 and 1 of every packet are the **source** and **destination** vports:

```
vport = (stream_type << 4) | port
```

- **Client side.** The Rendez-Vous client registers stream type 3 with port 0, which means "auto-allocate". Ports are tried from 15 downwards (`0x00679120`), which gives `0x3F`.
- **Server side.** The server's port is the `sid` from its URL, which is 1 for both default URLs, so the server is `0x31`.
- **Replies.** Use `src = 0x30 | sid` and `dst =` the client's `src` byte (echo it rather than hard-coding `0x3F`). Send from the IP and UDP port the client sent to.

On receive, the client handles a datagram like this:

1. It finds the stream by the full **destination** byte. If no stream matches, the packet is dropped silently (`0x006778c0`).
2. It checks the packet signature against the **source IP and UDP port** (§4.1), so a reply from the wrong port fails here as "Bad Signature".
3. It finds the connection by the key **(sender IPv4, sender UDP port, `src & 0x0F`)** (`0x0065c7e0`). The session id and stream nibble are not part of the key.
4. The **source** byte's stream nibble (`byte0 >> 4`) is not validated. It only selects the checksum seed and the payload codec, on both send and receive. Server packets must therefore carry exactly `0x3_` in byte 0, or they are checksummed and decoded with the wrong settings.

### 2.2 Sockets

The client binds `0.0.0.0:9103`. The socket is overlapped, non-blocking and has `SO_BROADCAST` set. If the bind fails it uses an OS-assigned port and advertises that port instead, so always reply to the observed source address.

The receive buffer holds exactly 1000 bytes. Larger datagrams from the server are lost, and the client never sends larger ones.

The Net-Z peer-to-peer stream (`0x11`) shares this socket. LAN discovery uses UDP 9100 (stream 6). Neither concerns the master server.

### 2.3 StationURL

Grammar (`0x006722f0` and helpers):

```
url    = scheme ":/" param *(";" param) ["#" param *(";" param)]
param  = key "=" value
```

- **Keys.** Exact and case-sensitive; the first match wins. Only these exist: `address`, `port`, `stream`, `sid`, `PID`, `CID`, `type`, `RVCID`.
- **Private part.** Parameters after `#` are invisible to all getters.
- **Values.** Integers are parsed with `atol` and default to 0. `port` is used as u16 and `sid` as u8; `sid` must be 1..15.
- **address.** Tried as a dotted IPv4 first, then resolved with blocking `gethostbyname`.
- **Scheme.** Selects the transport: `prudp` → PRUDP, `prudps` → secure PRUDP. The `stream` key is ignored for backend connections.
- **`type`.** A bit field: 2 = public. NAT traversal is attempted only when bits 1 and 2 are both set. Servers should advertise `type=2`.

URLs the client uses:

| Purpose | URL |
|---|---|
| Authentication server | `prudp:/address=<RENDEZVOUS_URL>;port=<RENDEZVOUS_PORT>;stream=3;sid=1;type=2` |
| Secure server | Taken from the Login response (`RVConnectionData.urlRegular`). Only an empty string triggers the fallback `prudps:/address=<RENDEZVOUS_URL>;port=<RENDEZVOUS_PORT+1>;stream=3;sid=1;PID=2;CID=1;type=2`. |

Requirements for the secure URL:

- **Address.** It needs a non-empty address, a non-zero port and a non-zero `sid`. The client dials only those three fields.
- **`PID`.** It must be non-zero: it is the secure server's principal id and the target of `RequestTicket`. With `PID=0` the client fails with `0x8001000D`.
- **Different endpoint.** The authentication connection is still open during the secure connect. So the secure server (and the special server, if any) must differ from it in at least one of (IP, UDP port, `sid`). Otherwise `Connect` fails with `0x80050002`.
  - Using port+1, as the fallback does, works.
  - So does the same port with `sid=2`. The client then sends `dst 0x32` and expects `src 0x32`.

## 3. Packet format

### 3.1 Datagrams

A datagram carries one or more packets.

- **Without `HAS_SIZE`**, a packet runs to the end of the datagram, so it must be the last one and nothing may follow it.
- **With `HAS_SIZE`**, its length is `header + 2 + size + 1`, and another packet may follow.
- **What the client sends.** On stream 3 it always sends one packet per datagram and never sets `HAS_SIZE`.
- **What the client accepts.** It does accept bundles. A server may bundle by setting `HAS_SIZE` on every packet except the last.
- **Parse errors.** Parsing stops at the first invalid packet. Packets before it are kept, the rest is lost.
- **No prefix.** There is no datagram prefix: a 2-byte length-prefix mode exists in `QueuingSocket` but is never enabled.

### 3.2 Header

All integers are little-endian. Serializer `0x0067a960`, parser `0x0068fbe0`.

| Offset | Size | Field | Present in |
|---|---|---|---|
| 0 | 1 | source vport | all |
| 1 | 1 | destination vport | all |
| 2 | 1 | `type` (bits 0–2) and `flags` (bits 3–7) | all |
| 3 | 1 | session id | all |
| 4 | 4 | packet signature | all |
| 8 | 2 | sequence id | all |
| 10 | 4 | connection signature | SYN and CONNECT, including their ACKs |
| 10 | 1 | fragment id | DATA, including DATA-ACK |
| next | 2 | payload size: wire bytes, including the codec byte | only with `HAS_SIZE` |
| next | n | payload | if the packet has a payload object (§3.4) |
| last | 1 | checksum (§3.3) | all |

| Type | Name | Notes |
|---|---|---|
| 0 | SYN | |
| 1 | CONNECT | |
| 2 | DATA | |
| 3 | DISCONNECT | |
| 4 | PING | |
| 5 | USER | Connectionless datagram, used by LAN discovery. Not used by Rendez-Vous. |
| 6, 7 | "ACK", "RESET" | Names in the trace printer only. The game never sends them. Do not send them either: the client would still ACK them and they would refresh its timers. |

| Flag | Name | Meaning |
|---|---|---|
| `0x08` | ACK | acknowledgment |
| `0x10` | RELIABLE | uses the reliable sequence space, window and in-order queue |
| `0x20` | NEED_ACK | the receiver must acknowledge |
| `0x40` | HAS_SIZE | a size field follows the optional fields |
| `0x80` | (unnamed, "V" in traces) | never set on Rendez-Vous traffic |

Byte 2 values to use (anything else breaks something, see §5 and §6):

| Packet | Byte 2 | Packet | Byte 2 |
|---|---|---|---|
| SYN | `0x20` | SYN-ACK | `0x08` |
| CONNECT | `0x31` | CONNECT-ACK | `0x09` |
| DATA (reliable) | `0x32` | DATA-ACK | `0x0A` |
| DISCONNECT | `0x33` | DISCONNECT-ACK | `0x0B` |
| PING | `0x24` | PING-ACK | `0x0C` |

**Minimum sizes**:
- 11 bytes: PING, DISCONNECT, USER and their ACKs;
- 12 bytes: DATA-ACK;
- 15 bytes: SYN, CONNECT and their ACKs.

A SYN or CONNECT without a payload is exactly 15 bytes. A parser must accept that.

**The client never initialises the connection signature** in its SYN or in any ACK it builds; the bytes are leftover heap memory. A server must ignore the connection signature everywhere except in a CONNECT without the ACK flag. Because of this, the checksum of the client's SYN varies from run to run.

**The client's parser trusts lengths.** It does not reject unknown types or wrong stream nibbles. It ignores a failed read of the optional field, and it allocates memory for whatever size a `HAS_SIZE` field claims. A truncated packet or an over-claiming size field can crash the client. Never send them, and do not reuse the game's parser to validate input.

### 3.3 Checksum

One trailing byte covers every preceding byte of the same packet, payload included as it appears on the wire. Code: `0x0065b300`; append `0x0065b620`; verify `0x0065b370`.

```python
def checksum(data: bytes, seed: int) -> int:
    n = len(data) & ~3
    words = sum(struct.unpack_from('<%dI' % (n // 4), data)) & 0xFFFFFFFF
    return (seed + sum(data[n:]) + sum(struct.pack('<I', words))) & 0xFF
```

**Seed.** It is chosen per stream type from the source byte's high nibble (`0x8c1b58 + 0x50*type`, offset 0).

- Stream 3 uses `sum(access_key) & 0xFF`. For `7fas5` that is **`0xA6`** (`0x007998b0` → `0x0064a3a0`).
- Every other stream uses 0.

The access key comes from `RENDEZVOUS_PASSWORD`. It is not hashed and is not used for any signature or key; a `"%s%d"` variant exists but is dead code.

Packets with a bad checksum are dropped silently.

The seed only adds to the result. So a server can recover the seed a client used: `(last_byte - checksum(packet[:-1], 0)) & 0xFF`. This is useful for spotting clients with a different access key.

### 3.4 Payload codec (stream 3)

Stream 3 has a Quazal LZO 1.08 plugin installed as its payload transform (`0x0065a0c0`). The serializer applies it to every packet that has a payload object, whatever its type: DATA, CONNECT, CONNECT-ACK and USER.

| Payload object | Bytes on the wire |
|---|---|
| none | nothing |
| present, empty | `00` |
| present, n bytes | `flag` + body. `01` = LZO1X-1 stream; any other flag = stored. The game writes `00`. |

**Encoding.**
- The client compresses when the LZO output is strictly shorter than the input (`0x006797f0`). About a third of its RMC messages are compressed, so **a server must implement LZO1X decompression**.
- **Every payload a server sends must start with the codec byte**, and `00` is the safe choice. If the byte is left out, the client takes the first payload byte as the flag. An RMC message starts with the low byte of its length, so a message of length ≡ 1 (mod 256) would be read as LZO.
- **Never send malformed `01` data.** On any decompression error the client retries with a doubled buffer, forever, until allocation fails and it crashes (`0x006798f0`). This includes a lone `01` byte and a valid stream followed by extra bytes.

**Order.**
- On send: fragment the message → encode each fragment → write the header and optional size → checksum.
- On receive: checksum → decode non-empty payloads → reassemble.

**Retransmissions are re-encoded and can differ byte for byte**, because the LZO dictionary is not reset. Deduplicate by sequence and fragment id, never by payload bytes.

**No encryption.** Rendez-Vous traffic is not encrypted, on `prudp` or `prudps`.
- The RC4 class's default key `CD&ML` does exist: it is built from immediate bytes in `0x00794ea0`, which is why no string search finds it. But it is not on this path.
- RC4 is only used inside Kerberos blobs (§7) and by peer-to-peer and LAN code. The game's peer-to-peer key is `"CommandosStrikeForceHasTheMostSecureKeyDontYouThink?"` reversed, plus its NUL.

### 3.5 Examples

These packets were produced by the game's own serializer under Unicorn and re-encoded byte for byte by an independent encoder.

The values are example choices:
- client session `5A`, server session `C3`;
- server-issued signature `0A1B2C3D`, client-issued signature `11223344`.

The checksum seed is `0xA6`. The client's SYN is shown with its uninitialised connection signature as zeros.

```
SYN              C→S  3f 31 20 00 00 00 00 00 00 00 00 00 00 00 36
SYN-ACK          S→C  31 3f 08 00 00 00 00 00 00 00 3d 2c 1b 0a ac
CONNECT          C→S  3f 31 31 5a 3d 2c 1b 0a 01 00 44 33 22 11 da
CONNECT-ACK      S→C  31 3f 09 c3 44 33 22 11 01 00 00 00 00 00 00 8d             (auth: empty payload object → 00)
CONNECT-ACK      S→C  31 3f 09 c3 44 33 22 11 01 00 00 00 00 00 00 04 00 00 00 79 56 34 12 a6
                                                                                  (prudps: Buffer(check+1), check = 0x12345678)
DATA             C→S  3f 31 32 5a 3d 2c 1b 0a 02 00 00 00 01 02 03 04 05 06 07 08 56
DATA-ACK         S→C  31 3f 0a c3 44 33 22 11 02 00 00 8f
DATA (LZO)       S→C  31 3f 32 c3 44 33 22 11 01 00 00 01 02 00 00 00 00 00 20 1a 00 00 11 00 00 04
                                                                                  (64 zero bytes, compressed by the game)
PING             C→S  3f 31 24 5a 3d 2c 1b 0a 01 00 23
PING-ACK         S→C  31 3f 0c c3 44 33 22 11 01 00 90
DISCONNECT       C→S  3f 31 33 5a 3d 2c 1b 0a 03 00 34
DISCONNECT-ACK   S→C  31 3f 0b c3 44 33 22 11 03 00 91
```

## 4. Signatures and session ids

### 4.1 Connection signatures

Each `PRUDPTransport` owns a private 16-byte HMAC-MD5 key. It is derived from the clock when the transport is created (`0x0067c3a0`), and the client creates a new transport at every login. The transport's signature for a peer address (`0x0067c4c0`) is:

```python
def sig(key: bytes, ip: str, port: int) -> int:
    msg = struct.pack('<IH', int(ipaddress.IPv4Address(ip)), port)  # host-order IPv4 as LE u32, port as LE u16
    return struct.unpack('<I', hmac.new(key, msg, hashlib.md5).digest()[:4])[0]
```

**Receive gate** (`0x0065daa0`). For every packet except SYN and USER, the receiver computes `sig(own_key, sender ip:port)`. If the packet's signature field differs, the packet is dropped silently; the client only writes a trace, `Bad Signature (%x,%x)`.

**Exchange.** Each side tells the other what to put in the signature field:
- The server's value `S` for the client travels in the SYN-ACK's connection signature.
- The client's value `C` for the server travels in the CONNECT's connection signature.
- From then on, every packet a side sends carries the **peer-issued** value (`0x0067b600`). This includes CONNECT, every ACK, DATA, PING and DISCONNECT.

| Field | Who fills it | Value |
|---|---|---|
| SYN signature / session / connection signature | client | `0` / `0` / uninitialised. Ignore the connection signature. |
| SYN-ACK signature / session / connection signature | server | `0` / `0` / `S` |
| CONNECT signature / connection signature | client | `S` / `C` |
| All later server → client packets | server | signature `C` |
| All later client → server packets | client | signature `S` |
| CONNECT-ACK connection signature | server | not read by the client; the game leaves it uninitialised, `0` is fine |

Rules that follow for a server:

- **`S` must be deterministic per client address.**
  - Every SYN-ACK the client receives, even while connected, replaces the value it stamps on later packets.
  - CONNECT is sent once, and its retransmissions keep the signature from the first send.
  - HMAC-MD5 with a server key, as in the game, is a good choice.
- **`C` belongs to one connection.** Store the value from the CONNECT and use it for that connection only.
  - It changes at every client login, because the client builds a new transport.
  - It also differs per server address, because the server's IP and port are part of the HMAC input; the secure server gets its own value.
  - Echoing `S` back to the client is dropped.
- **The client checks almost nothing in a SYN-ACK.** It ignores the signature, session and seq. Only four things matter:
  - the checksum seed;
  - the source byte, whose port nibble must equal the URL `sid`;
  - the exact source IP and port;
  - type SYN with flags exactly `0x08`.
- **Do not set NEED_ACK on the SYN-ACK.** The client would ACK it, sending an extra packet before its CONNECT.
- **Behind NAT,** both sides compute signatures over the address they actually see. *(inferred)*

### 4.2 Session ids

- **Own id.** Each endpoint picks a non-zero byte: the game uses the low byte of its millisecond clock, with 0 replaced by 1. It writes this id into byte 3 of every packet it sends; the SYN and the stateless SYN-ACK carry 0.
- **Learning the peer's id.** It is taken from the first non-SYN packet while the stored value is 0. After that, packets with a different id are dropped silently, before they are acknowledged.
- **The server's id must be a non-zero constant.** A CONNECT-ACK with session 0 leaves the client unlocked, and it then adopts whatever the next packet carries.
- **Exception.** A CONNECT without ACK that carries a new session id faults and removes the existing connection, and is not acknowledged. The client's next retransmitted CONNECT then creates a fresh connection.

## 5. Connection lifecycle

### 5.1 Handshake

```
Client (0x3F)                                                   Server (0x31)
SYN          20  sess=0   sig=0  seq=0  csig=<junk>          ──►
             ◄──  SYN-ACK      08  sess=0   sig=0  seq=<echo>  csig=S
CONNECT      31  sess=c   sig=S  seq=1  csig=C  [payload]    ──►
             ◄──  CONNECT-ACK  09  sess=s   sig=C  seq=1       [payload]
```

**SYN.**
- The connecting side sends SYN and retransmits it with linear back-off. With no RTT sample yet, that is at 0, 250, 750, 1500, 2500 and 3750 ms.
- The CONNECT inherits the same deadline. The whole exchange must finish within 5000 ms divided by the number of URLs, or the connect fails with `0x80050007` and the client tries the next URL. A late CONNECT-ACK is ignored.
- The transport answers every SYN that lacks the ACK flag **statelessly**: it creates no connection, and duplicate or late SYNs get the same answer.

**CONNECT.**
- The client builds it on the first SYN-ACK and sends it once, reliably.
- Payload:
  - *authentication connection:* none;
  - *`prudps`:* the ticket blob (§7).

**Server side (the game's accepting code, `0x0065d9d0` / `0x0067b6e0`).**
1. Check the signature.
2. Create the connection.
3. Pass the CONNECT payload to the transport's validation handler. The handler either rejects it, in which case the connection is removed and **nothing is sent**, or replaces the payload with a response.
4. Store `C` and the peer session.
5. Send a CONNECT-ACK carrying the possibly replaced payload buffer.
6. Start keep-alive.

**The CONNECT-ACK must**:
- **Echo seq 1 and be type CONNECT with flags exactly `0x08`.**
  - With another seq the client still switches to *connected*, but the CONNECT stays in its window. Its reliable sending then stalls for good after 7 more packets.
  - With RELIABLE set, the ACK takes the client's receive slot 1, and the server's first DATA is ACKed but thrown away.
- **Carry the right payload.**
  - *authentication:* the game sends the single codec byte `00` (an empty payload object). No payload at all also works, and the client ignores it.
  - *`prudps`:* `Buffer(check+1)`, see §7.

**Client on CONNECT-ACK.**
- It copies the payload into the connect-response buffer, but only for the first CONNECT-ACK.
- It frees the CONNECT from its window and switches to *connected*.
- It starts keep-alive and replies nothing.

**Duplicate CONNECT.** The game's own accepting code ACKs a duplicate with the duplicate's raw payload, without running the validation handler again. That would break a `prudps` client whose first CONNECT-ACK was lost. A server should instead:

- **Same session byte and same `C` as the existing connection:** treat it as a retransmission and resend the cached CONNECT-ACK.
- **Same `(ip, port, src nibble)` but a different session byte or `C`:** it is a restarted client. Drop the old connection without replying, as the game does, and accept its next CONNECT as new.
  - Comparing only the session byte is not enough: a restarted client draws the same byte about once in 255 reconnects.
  - That case was reproduced in emulation: the old cached ACK carries the wrong signature, and the new client cannot connect.

**Rejecting a `prudps` CONNECT.**
- The library's own server sends nothing, so the client fails only at its 5000 ms deadline.
- A replacement can answer with a wrong check value instead, and the client fails immediately with `0x8005000A`.

Endpoint states (`+0x20`):

| State | Meaning |
|---|---|
| 0 | not connected |
| 1 | connecting |
| 2 | connected |
| 3 | disconnecting |
| 4 | faulty |

An accepted (server-side) connection stays in state 0 with a "peer connected" flag set.

### 5.2 Keep-alive and silence

Both sides arm a ping timer after the handshake. Its period is `KeepAlive`; the Rendez-Vous client sets 10 000 ms, the default is 1 000 ms. On each tick (`0x0067bc10`):

1. If nothing has been received for more than `MaxSilence` (Rendez-Vous client: 60 000 ms, default 5 000 ms), the connection faults with code 2. Nothing is sent to the peer.
2. Otherwise, if no **new** reliable packet has been sent for `KeepAlive`, it sends PING (`0x24`, unreliable, NEED_ACK) with `seq = ++ping_seq`. The first ping has seq 1.

**Keeping the client alive.** Any valid packet the client receives resets its silence timer, ACKs included. So ACKing its pings is enough; a server does not need to ping.

**Client silence.** The ping test runs once per tick, so an idle client can stay silent for almost 20 s (2 × KeepAlive).

**The client never tells the server that it gave up.** A server must expire silent sessions itself. Use at least 60–70 s, never something close to 10 s.

**After a silence fault, the client's own endpoint stays registered in state 4.** It blocks a new connection to the same server address until the game releases it. This is a client limitation; a server avoids it by never going silent.

### 5.3 Disconnect

**Who can send a DISCONNECT.** Any endpoint in state 2 (*connected*), whichever side opened the connection. `Disconnect` (`0x0067bcd0`) checks only the state, and it is the only place in the binary that builds a DISCONNECT.
- An endpoint that was only accepted stays in state 0. Its `Disconnect` returns `0x80050002` and sends nothing.
- Towards the Rendez-Vous servers the game is always the connecting side, so in that direction only the game sends DISCONNECT.
- Net-Z peers sometimes also connect back to a peer they accepted (`0x00668370`). Such an endpoint reaches state 2 and can disconnect too.

**Sending.**
- The endpoint sends one reliable DISCONNECT (`0x33`) with the next reliable seq, so 2 right after the handshake.
- The DISCONNECT is held back while reliable DATA is unacknowledged.
- Both the DISCONNECT-ACK and the 5000 ms deadline count as success.
- At logout the game disconnects the authentication, special and secure connections in that order, each only if it is still connected (`JobBackEndServicesLogout`). No RMC message is sent.

**Receiving a DISCONNECT.** The receiver ACKs it (`0x0B`). Like any reliable packet it uses up one receive seq slot and resets the silence timer. What happens next depends on whether the remote side connected to this endpoint:

| Endpoint | After the ACK |
|---|---|
| The remote connected to it (server role) | Reports "peer disconnected" and removes the connection. A later retransmitted DISCONNECT gets no ACK. |
| It connected to the remote, and the remote never connected back (the game towards Rendez-Vous) | Fires no callback, stays *connected*, and keeps the endpoint registered. A retransmitted DISCONNECT is ACKed again. |

**A DISCONNECT from the server does not end the game's session.**
- The game notices a lost back-end only by checking, every frame, that its authentication and secure connections are still in state 2 (`0x00797d90`).
- If one is not, the game logs out and returns to the menu with an error code.
- A server DISCONNECT leaves the client's connection in state 2, so nothing happens.
- To drop a client, stop answering. After the 60 s silence timeout its connection faults and the game logs out. Until then expect its pings and retransmissions.

**Peer-to-peer (Net-Z) teardown.** This does not concern the master server.
- A station that only accepted a connection cannot send DISCONNECT.
- Instead it asks the connecting peer to leave with an application-level EOS message: ObjDup type `0xFF` (`0x0065f5f0`).
- That peer then disconnects at the PRUDP level itself (`JobDisconnectStation`).

**Unknown connections and RESET.** There is no RESET. Packets for unknown connections (DATA, PING, DISCONNECT, any ACK) are dropped silently.

## 6. Reliability

### 6.1 Sequence numbers

Sequence ids are u16 and compared with serial arithmetic (half range `0x8000`).

| Packet | Sequence |
|---|---|
| CONNECT, reliable DATA, DISCONNECT | One reliable counter per direction, starting at 1 (`0x0068d3e0`).<br>**Client:** CONNECT = 1, first DATA = 2. A server should therefore expect CONNECT.seq + 1 next.<br>**Server:** first DATA = 1; the client expects 1 first. |
| Unreliable DATA | Separate counter starting at 0. Only feeds a "packets lost" statistic. |
| PING | Separate counter. The first ping is 1. |
| SYN | Always 0, including retransmissions. |
| ACK | Echoes the acknowledged packet's seq (and fragment id for DATA). |

### 6.2 Acknowledgments

Every packet with NEED_ACK that passes the signature and session checks is acknowledged **immediately**, before any other processing. Duplicates and old packets are acknowledged again. A server should do the same.

The ACK has:
- the same type and flags = ACK only;
- the same seq and fragment id;
- no payload, except the CONNECT-ACK (§5.1).

There are no cumulative, delayed, piggy-backed or multi-ACKs.

The sender finds the acknowledged packet by seq alone (`0x0067b530`); the type in the ACK only selects the handler.

**Never set RELIABLE on an ACK.** The client puts every packet that carries `0x10` into its in-order receive queue, whatever its type.

**Reliable DATA must use `0x30`.** DATA with `0x20` alone is delivered immediately, out of order and without reassembly.

### 6.3 Retransmission

RTT estimator (`0x0068d460`, `0x0068d4d0`), all values in ms:

```
first sample m:  avg8 = 8*m; dev4 = 0
next samples:    err = m - (avg8 >> 3); avg8 += err; dev4 += |err| - (dev4 >> 2)
RTO = 250                          until the first sample
RTO = (dev4 & ~3) + (avg8 >> 3)    afterwards (SRTT + 4*RTTVAR, no lower or upper limit)
```

Example: samples 100, 120, 80, 300 give RTO 100, 122, 135, 352.

**Which packets are sampled.** Only packets that were sent exactly once (Karn's rule), plus PING-ACKs that match the current ping. The first sample usually comes from the CONNECT-ACK.

**Back-off.** It is linear: the n-th transmission waits `n × RTO`. A packet is sent at most **20** times. Timers are serviced at most every 10 ms.

When a packet runs out of transmissions (`0x0067c130`):

| Packet | Result |
|---|---|
| DATA | Silently abandoned. It stays in the window with no fault and no callback. The next message goes out with the next seq, so the receiver's in-order stream has a permanent hole. |
| SYN / CONNECT | The connect fails. |
| DISCONNECT | The disconnect completes (as success). |

**No lower bound on RTO matters a lot on a LAN.** A server that ACKs instantly drives the client's RTO towards 0. A lost or delayed packet is then retransmitted on every 10 ms tick and abandoned after about 0.2 s, while pings keep the connection alive.

The real game showed this on localhost: it resent a DATA packet 7 ms after the first copy, although the server's ACK had gone out at once.

A server must therefore:
- **ACK immediately in its receive path**, before processing the RMC call, and never stall. A pause of a fraction of a second can permanently break a LAN client's reliable stream.
- **Consider detecting a persistent gap** in a client's reliable stream and dropping that session.

**Server-side retransmission is the implementer's choice.** The client accepts bursts: 257 back-to-back fragments, out of order and duplicated, were all delivered correctly in emulation. A server still needs a retransmission timer for lost DATA, and mirroring the client's policy (above) is reasonable.

### 6.4 Window

The window is 8 packets (`0x0068c950`). There is no flow control, and the send queue grows without limit.

**Releases.** The game releases at most one packet from the window per `Send` call and one per accepted ACK. So after a loss only about one packet is in flight until new messages are sent.

**Never ACK a seq you did not receive.** An ACK for a sequence number that is queued but not yet sent removes it, and the client's reliable stream then stalls forever.

### 6.5 Receive ordering

**Reliable packets** (`0x0068c4d0`):
- Packets older than the expected seq, or already buffered, are dropped. They have already been acknowledged.
- Future packets are buffered without limit.
- The buffer is drained in order, and only DATA is delivered upwards. CONNECT and DISCONNECT use up their slot.

**Unreliable DATA** is delivered immediately, even when late or duplicated.

### 6.6 Fragmentation

**Splitting.** The client splits reliable messages longer than **974 bytes** (`0x0067be90`). The limit is 1000 minus 18 bytes of maximum header overhead minus 8.
- Fragment ids start at 1 for every message and run 1, 2, …, 255, then wrap to 1 (0 is skipped). The **last fragment has id 0**.
- A message that fits in one packet uses 0.
- Unreliable messages over 974 bytes are refused.

**What the client accepts from a server.** Up to 987 stored bytes per DATA packet: a 1000-byte datagram minus 11 bytes of header, 1 codec byte and 1 checksum byte. 974 is always safe.

**Reassembly** (`0x0067acc0`) applies to reliable DATA after ordering. Each fragment is decoded separately before concatenation.
- Pieces are appended while the fragment id equals the expected one, which starts at 1.
- Id 0 completes the message and resets the expected id to 1.

**Numbering errors do not heal.** A wrong id discards the partial message but leaves the expected id unchanged. The next fragment with id 0 is then delivered alone, as a truncated message. A first fragment numbered 0 is delivered as a complete message.

So server fragments must:
- be RELIABLE;
- use consecutive seqs;
- be numbered 1, 2, … with the last one 0;
- never be interleaved with another message's fragments;
- each carry their own codec byte.

## 7. Secure connections (prudps)

`prudps` uses the same stream-3 PRUDP transport. `SecureEndPoint` only forwards calls (`0x0079b9d0`). The only difference is the CONNECT exchange; after the CONNECT-ACK the connection behaves exactly like `prudp`. There is no session-key encryption or signing of later packets.

**CONNECT payload** (`0x0079bf20`), before the §3.4 codec byte. A Buffer is a u32 LE length followed by the bytes:

```
Buffer(ticket) || Buffer(Kerberos(session_key, u32 pid || u32 cid || u32 check))
```

- `ticket` and `session_key` come from `RequestTicket` (see [rendezvous.md](rendezvous.md)).
- `pid` is the client's own PID from Login.
- `cid` is the client's connection id, 0 at login.
- `check` is random.
- The second Buffer is always 28 bytes: 12 bytes of plaintext and a 16-byte HMAC.
- The CONNECT cannot be fragmented, so keep tickets small. The library's 36-byte ticket gives a 73-byte payload.

**Required CONNECT-ACK payload** (`0x0079c230`). It must start with `Buffer(u32 check + 1)`, i.e. `04 00 00 00` + LE(check+1 mod 2^32), after the codec byte `00`.
- Extra bytes inside or after the Buffer are tolerated.
- No +1, an empty payload, or `Buffer(0)` fails the connection with `0x8005000A`.
- **Keep the length prefix.** The client allocates the declared length before checking it. A bare u32 without the prefix makes it allocate up to 4 GB and can crash it.

**Kerberos blob** (`0x0079dfd0` / `0x0079e090`):

```
Kerberos(K, plaintext) = RC4(K, plaintext) || HMAC-MD5(K, ciphertext)
```

- RC4 is textbook, with no dropped bytes. The keystream restarts for every blob.
- Decryption checks the 16-byte HMAC first.
- Key lengths: the HMAC needs 8..64 bytes; the protocol uses 16.

**The library's own accepting code** (`0x0079c000`) shows the reference behaviour:
1. Decrypt the ticket with its own key. Ticket format: `Kerberos(server_key, u32 user_pid || session_key[16])`.
2. Decrypt the request with the session key. **It does not check the result of the HMAC check, and it ignores the request's PID.**
3. Take the PID from the ticket and `cid` and `check` from the request.
4. Answer `Buffer(check+1)`.

**A replacement server should be stricter:**
- verify the request's HMAC with the session key;
- compare the request's PID with the ticket's PID;
- expect `cid = 0` at login.

Since the client never looks inside the ticket, the server may use any ticket format it can decode.

Test vector (from the game's code, reproduced independently):
- key = `00 01 … 0f`
- plaintext = `3412000001000000efbeadde`
- Kerberos blob = `dd8e40f946e219cce9653a18405a40a6960d5597dc663e6a82cc6e48`

## 8. Server checklist

1. **Sockets and vports.**
   - Listen on UDP 30065 for authentication.
   - Run the secure server on another port (e.g. 30066) or another `sid`.
   - Reply from the socket the client used, with `src = 0x30 | sid` and `dst =` the client's `src`.
2. **Checksum and size.** Verify and append the checksum with seed `0xA6`. Keep every datagram ≤ 1000 bytes. Never send truncated packets or over-claiming size fields.
3. **Payloads.**
   - Prefix **every** payload you send with `00`. Never send `01`.
   - Decompress client payloads that start with `01` (LZO1X).
   - Deduplicate by seq and fragment id, not by bytes.
4. **SYN.** Answer every SYN without ACK statelessly with a SYN-ACK:
   - byte 2 exactly `0x08`;
   - seq echoed;
   - signature 0 and session 0;
   - connection signature `S(client ip:port)`, deterministic per address.
5. **CONNECT.**
   - Accept it when its signature equals `S`.
   - Store the client's `C` and session byte, and pick your own constant non-zero session byte.
   - Reply with a CONNECT-ACK: byte 2 `0x09`, seq 1 echoed, signature `C`, never RELIABLE.
   - Payload: `00` for authentication, `00` + `Buffer(check+1)` for `prudps`.
   - Cache the CONNECT-ACK. Resend it only for a CONNECT with the same session byte and `C`; otherwise reset the connection (§5.1).
6. **ACKs.** ACK every NEED_ACK packet immediately, before processing it: same type, flags `0x08`, seq and fragment id echoed. This includes PING and duplicates. Never ACK a seq you did not receive.
7. **Sending.**
   - Stamp signature `C` and your session byte in every packet.
   - Reliable DATA is `0x32`, numbered from 1.
   - Fragment at 974 bytes, with consecutive ids and the last fragment 0.
   - Retransmit unacknowledged packets with back-off.
8. **Timeouts.**
   - Expire a session after 60–70 s without a valid packet.
   - Treat a client DISCONNECT as the end of the session: ACK it, then drop state.
   - Drop packets for unknown connections silently.
   - To kick a client, stop answering.

## 9. Differences from later Quazal PRUDP v0

This comparison is from public descriptions of later Quazal games (Nintendo NEX, Ubisoft titles). It was not re-checked against those games.

| Aspect | This build | Later v0 variants |
|---|---|---|
| Type and flags | 1 byte: 3-bit type, flags in bits 3–7 | NEX: 2 bytes, 4-bit type, flags shifted further |
| Packet signature | Always the peer-issued connection signature | NEX: HMAC of the payload for DATA, constant for empty packets |
| Connection signature | HMAC-MD5 of the peer address with a private random key | similar idea |
| Checksum | Byte sum of the access key + word sum | same algorithm |
| Payload | LZO flag byte, no encryption | RC4 (`CD&ML` or session key) and zlib in several titles |
| ACKs | One ACK per packet | NEX adds aggregated ACKs |
| Teardown | One reliable DISCONNECT, no RESET | Several DISCONNECTs in some variants |

## 10. Open questions

- **Original server behaviour.** How the Eidos servers behaved — whether they pinged, sent DISCONNECT, used the special connection, or which ticket format they used — is not in the client. The checklist mirrors the game's own accepting code, with the fixes noted above.
- **Beyond localhost.** The live test ran over loopback. Packet loss, NAT and datagrams over 1000 bytes have not been observed on a real network; the drop of large datagrams is inferred from how `WSARecvFrom` behaves on Windows.
- **Flag `0x80`.** Its meaning (trace letter `V`, possibly voice) is unknown. Rendez-Vous traffic never uses it.
- **Abandoned DATA.** What the RMC layer does after reliable DATA is silently abandoned was not traced.

## Appendix: key functions

| Address | Function |
|---|---|
| `0x0067a960` | `PacketOut::Serialize`: header, optional fields, codec, checksum |
| `0x0068fbe0` | `PacketIn::Parse`: header, size framing, checksum, codec |
| `0x0067a700` | `PacketOut` ctor (endpoint, type, flags, payload) |
| `0x0065b300` | Checksum |
| `0x007998b0` / `0x0064a3a0` | Stream-3 checksum seed = byte sum of the access key |
| `0x0064bb00` | Per-stream settings: seed, payload codec, bundling |
| `0x006797f0` / `0x006798f0` | Stream-3 LZO codec: encode / decode |
| `0x006893e0` / `0x00688e60` | Datagram receive loop / parse one packet |
| `0x0065daa0` | `PRUDPTransport::Receive`: signature gate, stateless SYN-ACK, CONNECT accept, dispatch |
| `0x0065d9d0` | Accept CONNECT and run the validation handler |
| `0x0065c7e0` | Endpoint lookup by (IP, port, `src & 0xF`) |
| `0x0067c3a0` / `0x0067c4c0` | Signature generator: key set-up / HMAC-MD5 of (IP, port) |
| `0x0067af10` | `PRUDPEndPoint` ctor: session id, prebuilt SYN, settings |
| `0x0067b2c0` | `Connect`: sends SYN with deadline |
| `0x0067b6e0` | Endpoint receive: signature/session learning, ACKs, per-type state machine |
| `0x0067b600` | Endpoint send: stamps signature and session, reliable window |
| `0x0067b150` | `SendReliable`: n × RTO back-off |
| `0x0067b530` | ACK processing: RTT sample, window release |
| `0x0067c130` | Retransmission timeout and give-up |
| `0x0067bc10` | Keep-alive and silence |
| `0x0067bcd0` | `Disconnect` |
| `0x0067be90` | Send DATA: fragmentation at 974 bytes |
| `0x0067acc0` | Reassembly and delivery |
| `0x0068d3e0` / `0x0068c950` / `0x0068cf70` | Sliding window: ctor / next to send / acknowledged |
| `0x0068c550` / `0x0068c4d0` | Receive queue: ctor / insert |
| `0x0068d460` / `0x0068d4d0` | RTT update / RTO |
| `0x00679c40` | Transport defaults: 20 transmissions, KeepAlive 1000, MaxSilence 5000, window 8 |
| `0x00799950` | Rendez-Vous `ServiceTransport`: stream 3, KeepAlive 10 000, MaxSilence 60 000 |
| `0x006778c0` / `0x00677a30` | `UDPTransport` receive dispatch / send (vports) |
| `0x00679120` | Local stream port allocation (15 downwards) |
| `0x007994b0` / `0x00799060` | Authentication URL / secure URL selection |
| `0x0079e990` | Secure URL parse and checks |
| `0x0079bf20` / `0x0079c230` / `0x0079c000` | `prudps` CONNECT payload / check+1 test / library's accepting validator |
| `0x0079dfd0` / `0x0079e090` | Kerberos encrypt / decrypt |

# Rendez-Vous over PRUDP in Commandos: Strike Force

This document covers what the Commandos: Strike Force client sends inside PRUDP DATA payloads: RMC messages, data types, the Rendez-Vous protocols it calls and the login sequence. It is written for people implementing a compatible server.

The source binary, the address conventions and the verification method are the same as in [prudp.md](prudp.md): addresses are functions in `CommXPC.exe`, and statements are confirmed by running the game's own code in emulation unless marked *(inferred)*.

The whole login described in §4, and account creation, were run end to end. A server model written from scratch drove the game's client code through Login, key derivation, RequestTicket, the `prudps` connect and Register.

The same server then worked with the real game: account creation, login with the new account and the lobby. See "Tested with the real game" in prudp.md.

## 1. RMC messages

**One RMC message per PRUDP message.**
- Every RMC message is one reliable PRUDP message, fragmented if long (prudp.md §6.6).
- The client parses exactly one RMC message per delivered PRUDP message and ignores trailing bytes.
- So never put two messages in one.

All integers are little-endian. A byte-swap switch exists in the code but is never enabled.

| Message | Layout |
|---|---|
| Request | `u32 size \| u8 protocol \| 0x80 \| u32 call_id \| u32 method \| arguments` |
| Success response | `u32 size \| u8 protocol \| u8 1 \| u32 call_id \| u32 method \| 0x8000 \| results` |
| Error response | `u32 size \| u8 protocol \| u8 0 \| u32 error \| u32 call_id` |

**Size field.** It counts the bytes that follow it. The client writes it correctly but does not check it on receive; write it correctly anyway.

**Call ids.** They come from one counter in the client that starts at 1. A server just echoes them.

**Example.** `Login("user")` with call id `0x1234`, as built by the game's own writer:

```
10000000 8a 34120000 01000000 0500 7573657200
size     10|0x80  call id   method 1  String "user"
```

**How the client handles responses** (`0x0067f1c0`, `0x006856d0`).
1. It looks up the protocol by the response's protocol byte. A protocol byte with bit 7 set is treated as a request.
2. It looks up the call id. Unknown or expired call ids are dropped.
3. It runs that protocol's result parser.
4. A **success response whose method id it does not expect triggers `int3`, i.e. a crash**. So echo `method | 0x8000` exactly.
5. An error response completes the call with that error code.

**How the client handles requests from the server** (`0x0067ef90`).
- A request for a protocol the client does not host gets error `0x80010002`. Protocol 1 is the exception and is ignored silently.
- An unknown method on a hosted protocol gets no response at all.

## 2. Data types

| Type | Encoding |
|---|---|
| `u8`, `u16`, `u32`, `u64` | Little-endian |
| `bool` | 1 byte, non-zero = true |
| `String` | `u16 length` (counting the NUL) + bytes + NUL. NULL is `00 00`; the empty string is `01 00 00`. Bytes are passed as-is, including high-bit characters. The length must include the NUL, because the client copies exactly that many bytes and does not terminate them. |
| `Buffer` | `u32 length` + bytes |
| `list<T>` | `u32 count` + items |
| `Result` | `u32`; success when bit 31 is clear. The success value is `0x00010001`. |
| `StationURL` | A `String` holding the URL text (prudp.md §2.3). Both empty encodings read as an empty URL. |
| `DateTime` | `u64` |
| `AnyDataHolder` | `String class_name \| u32 n+4 \| u32 n \| n bytes` of the object's own serialization |

**Always send exact lengths.** The client allocates and copies whatever length a String or Buffer declares before checking it against the data. A wrong length can make it allocate gigabytes or read stale memory.

## 3. Protocols

### 3.1 Called by the client

| Protocol | Method | Arguments → results |
|---|---|---|
| 10 Authentication | 1 Login | `String user` → `Result, u32 pid, Buffer response, RVConnectionData, String` |
| | 2 LoginEx | Never used by the game |
| | 3 RequestTicket | `u32 source_pid, u32 target_pid` → `Result, Buffer` |
| 11 SecureConnection | 1 Register | `list<StationURL>` → `Result, u32 cid, StationURL public_url` |
| | 2 RequestConnectionData | `u32 cid, u32 pid`. Only sent when a secure URL has no usable address. |
| 25 AccountManagement | 1 CreateAccount | `String name, String key, u32 groups, String email` → `Result`. `groups` is always 0. |
| | 4 ChangePassword | `String key` → `bool` |
| | 17 UpdateStatus | `String status`. Sent only in the account-creation flow: the game sends `"Online"` and throws the call away, so a reply is optional. |
| 21 MatchMaking | see §6 | |
| 50 MatchMakingExt | 2 GetParticipants | `u32 gid, bool` → `list<u32>` |
| 3 NATTraversal | 1 RequestProbeInitiation | `list<StationURL>` |
| | 2 InitiateProbe | `StationURL` |

`RVConnectionData` is `StationURL url_regular, list<u8> special_protocols, StationURL url_special`.

Which connection carries a call (`0x0079ad90`):
- protocol 10 → the authentication connection;
- protocol ids listed in `special_protocols` → the special connection;
- everything else → the secure connection.

So Register and all AccountManagement calls need a completed secure connection.

### 3.2 Hosted by the client (called by the server)

| Protocol | Method | Arguments | Client's reply |
|---|---|---|---|
| 14 NotificationEvents | 1 ProcessNotificationEvent | `u32 source_pid, u32 type, u32 param1, u32 param2, String` | Success, method `0x8001`, no data |
| 3 NATTraversal | 1 RequestProbeInitiation | `list<StationURL>`. The client looks up each target by `RVCID` and sends it `InitiateProbe` with its own URL. *(inferred)* | `0x8001`, no data |
| 3 NATTraversal | 2 InitiateProbe | `StationURL` | `0x8002`, no data |
| 22 (name unknown) | 1, 2 | `String`; `String, Buffer` | not traced |

A server is therefore expected to forward NAT probes between clients by `RVCID`, as later Quazal servers do. *(inferred)*

## 4. Login sequence

Main login, after `BackEndServices::Login` (`0x00797e30`). A normal login sends only Login, RequestTicket and Register. In the live test the game then went straight to the lobby (§6). `UpdateStatus("Online")` is sent only in the account-creation flow (§5).

1. **Authentication connection.** The client opens a PRUDP connection to `prudp:/address=<RENDEZVOUS_URL>;port=<RENDEZVOUS_PORT>;stream=3;sid=1;type=2` with a 5000 ms timeout (prudp.md §5.1). The CONNECT carries no payload.
2. **Login.** The client calls `10/1 Login(username)`. A successful answer has:
   - `Result = 0x00010001`;
   - the player's `pid` — **never 2**: that is the secure server's PID, and the client would then skip the rest of the login;
   - `response`: any valid Kerberos blob under `K_user` (§5). Even 16 bytes, i.e. the HMAC of empty plaintext, works;
   - `RVConnectionData`;
   - a `String`.
3. **Key check.** The client derives `K_user` from the typed password and `pid`. It then checks only the HMAC of `response`.
   - If the check fails, the login fails with `0x80030065` (wrong password).
   - The decrypted content is thrown away.
   - **So the password is checked on the client side.** The server never receives it and has to seal `response` with the right key.
4. **Secure URL.** It is `url_regular`, or, if that string is empty, `prudps:/address=<host>;port=<RENDEZVOUS_PORT+1>;stream=3;sid=1;PID=2;CID=1;type=2`. Requirements are in prudp.md §2.3.
5. **RequestTicket.** The client calls `10/3 RequestTicket(pid, <PID from the secure URL>)` on the authentication connection. The server answers `Result, Buffer`, where:

   ```
   Buffer = Kerberos(K_user, session_key[16] || u32 target_pid || Buffer(ticket))
   ```

   - The client ignores the Result.
   - The session key must be exactly 16 bytes.
   - The client caches the ticket under the decrypted `target_pid` but looks it up by the requested PID, so **echo the requested PID**.
   - The ticket itself is opaque to the client. The library's own format is `Kerberos(secure_server_key, u32 pid || session_key[16])`.
6. **Secure connection.** The client opens a `prudps` connection to the secure URL. The CONNECT payload carries the ticket; the server must answer `Buffer(check+1)` (prudp.md §7).
   - **This is the only place where the server authenticates the client:** the client proves it decrypted the RequestTicket answer, which requires `K_user`.
   - The server checks this by verifying the request's HMAC under the session key it issued.
7. **Special connection.** If `url_special` is non-empty, the client opens a third connection the same way, and the login fails if that fails. Send an empty `url_special` and an empty list.
8. **Register.** The client calls `11/1 Register(list<StationURL>)` on the secure connection.
   - The list holds its local `prudp` URLs, one per non-loopback IPv4 address, e.g. `prudp:/address=192.168.1.5;port=9103;sid=15`. These are private addresses; use the observed source address for anything public.
   - The server answers `Result, u32 cid, StationURL public_url`.
9. **Done.** The client appends `RVCID=<cid>` to `public_url` and registers protocol 14 locally. It is now logged in.
   - The authentication connection stays open.
   - Logout sends no RMC message.

Between steps 2 and 5 the client runs 65 000+ MD5 iterations as a time-sliced job, so expect a short pause.

**Reporting a login failure.**
- An RMC error response leaves the login job with a generic result `0x80010001`.
- To show a specific error, such as an unknown user, send a **success** response whose `Result` carries the error code, e.g. `0x80030064`.
- Still send all the other fields: the client reads them unconditionally.

## 5. Accounts and keys

**User key** (`0x0079e170`). The username is not used:

```python
def md5n(data, n):
    for _ in range(n):
        data = hashlib.md5(data).digest()
    return data

K_user = md5n(password_bytes, 65000 + (pid & 0x3FF))  # password without NUL, bytes as typed
```

**Account creation** (`0x006b0a10`):
1. The game logs in with username **and** password both set to `**CuentaCrearCSFv1**`. This is a full login, steps 1–9 of §4. So the server must provide this bootstrap account with a PID other than 2.
2. It sends `25/17 UpdateStatus("Online")`.
3. It sends `25/1 CreateAccount(name, key, 0, email)`, where `key` is the lowercase hex of `md5n(password, 65000)`. The plaintext password is never sent.
4. `25/4 ChangePassword` sends the same kind of hex key.

**Storing passwords.** A server can store `H = md5n(password, 65000)` and compute `K_user = md5n(H, pid & 0x3FF)` at login. This only needs the hash.

**Test vectors** (from the game's code, reproduced with `hashlib`):

| Input | Output |
|---|---|
| `K_user("password", pid 1234)` (65 210 iterations) | `111280a49a2108bd85ff293695e48566` |
| `K_user("7fas5", pid 2)` | `3c2a139d541dad614ee4b7427efa8d7e` |
| `H = md5n("**CuentaCrearCSFv1**", 65000)` | `a8caed774223459d40bf15fa2dc1976a` |
| `md5n(H, 0x1234 & 0x3FF)` = `K_user` of that account at pid `0x1234` | `bf37b175e9bbaaeed03b633747f715d2` |
| `md5n(b"P\xe4ss w0rd", 65050)` (high-bit bytes are hashed as-is) | `89b0f90beff86bdd32f959bf241f9d49` |

## 6. Matchmaking (summary)

The game's gathering class is `Commandos3DGameGathering`, sent as an `AnyDataHolder`. Its layout (`0x006b1b10`):
- the base Gathering fields: `u32 id, u32 owner, u32 host, u16 min, u16 max, u32 policy, u32 policy_arg, u32 flags, u32 state, String description`;
- followed by `u8, u8, bool, bool, u8[32], u8[512], u32`. The meaning of these extra fields is unknown.

Calls the game sends on protocol 21:

| Method | Arguments → results |
|---|---|
| 1 RegisterGathering | `AnyDataHolder` → `u32 gid` |
| 4 UpdateGathering | → `bool` |
| 11 Participate | `u32 gid, String` → `bool` |
| 12 CancelParticipation | `u32 gid, String` → `bool` |
| 13 GetParticipants | `u32 gid` → `list<u32>` |
| 16 GetParticipantsURLs | `u32 gid` → `list<StationURL>` |
| 19 FindByDescriptionRegex | `String regex, ResultRange{0xFFFFFFFF, 10}` → `list<AnyDataHolder>`. In the lobby the game sends the regex `^WAD.P` about every 3 s. An empty list (`u32 0`) is accepted (live test). |
| 26 LaunchSession | `u32 gid, String` → `bool` |
| 27 UpdateSessionURL | `u32 gid, String` → `bool` |
| 30 SetState | `u32 gid, u32 state` → `bool` |

**Hosting a game:**
1. `RegisterGathering`;
2. `Participate(gid, "")`;
3. `LaunchSession(gid, "")`;
4. `SetState(gid, 2)`;
5. periodic `UpdateSessionURL(gid, "")` (the period is passed as 600; its unit is unknown).

## 7. Result codes seen in the client

These names follow later Quazal numbering. The binary has no name table. *(inferred)*

| Code | Meaning |
|---|---|
| `0x00010001` | Success |
| `0x80010001` | Generic failure (initial value of a job's result) |
| `0x80010002` | Unknown protocol or method |
| `0x8001000D` | Secure URL without a PID |
| `0x80030064` | Unknown user (as sent by a server) |
| `0x80030065` | Login response failed the key check (wrong password) |
| `0x80050002` | Operation not allowed in the endpoint's current state (e.g. `Connect` on an endpoint that is already in use, `Send` before connecting) |
| `0x80050003` | Invalid URL |
| `0x80050007` | Connect timed out |
| `0x8005000A` | Secure CONNECT-ACK check failed |

## 8. Open questions

- **NotificationEvents.** Which event types the game subscribes to, and what their parameters mean.
- **Gathering fields.** The meaning of the extra `Commandos3DGameGathering` fields. Also what the lobby regex `^WAD.P` matches, presumably the description format of a hosted game.
- **Protocol 22.** Its name and purpose. It is server → client; strings and buffers suggest peer messaging.
- **Original server.** Whether the original server used the special connection, expired gatherings that were not refreshed, or required anything from `UpdateStatus`.
- **Skipping Kerberos.** A `prudp:/` (not `prudps:/`) `url_regular` probably makes the client skip RequestTicket and the secure CONNECT entirely, because the scheme selects the transport. This was not verified end to end.
- **Password encoding.** How the UI encodes the typed username and password before hashing. Bytes are hashed as-is.

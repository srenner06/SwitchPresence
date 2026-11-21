# Handoff: Switch 2 → Discord "Now Playing" (C# / .NET)

Planning handoff for implementing in Claude Code. It contains everything researched so far, the decisions made, the protocol details needed to build it, and the open questions. Nothing has been implemented yet.

---

## 0. Corrections from the live spike (2026-11-21)

§5 was written by reading nxapi's source, never by calling the servers. A spike has now run the
whole flow end to end successfully. Two things in §5 were wrong; the rest held exactly as written.

**1. `znca_version` is `3.6.0`, not `3.5.0`.**
The f-API runs worker pools pinned to specific NSO app versions and only keeps current ones.
Asking for a version with no workers fails the `/f` call with HTTP 406:
`{"error":"unsupported_version","error_description":"No matching workers available"}` — note this
happens at the f-API, *before* Coral ever sees a request, so Coral status `9427` is **not** the
symptom to watch for. The authoritative list is an endpoint §5 doesn't mention:

```
GET https://nxapi-znca-api.fancy.org.uk/api/znca/config
{"versions":[{"platform":"Android","version":"3.6.0","build":14330,"worker_count":3}],"nso_version":"3.6.0"}
```

This went stale between the nxapi commit §5 was read from (2026-09-09) and the first spike run, so
the version must be discoverable at runtime rather than only hardcoded. Note `X-znca-Version` is
*not* the header the API terms forbid setting dynamically — that is `X-znca-Client-Version`, which
stays static (`d8fAZDPzwimzQ7c6`, confirmed accepted).

**2. `/encrypt-request` returns raw bytes, not a JSON envelope.**
§5.4 step 2 says the response is `{"data":"<base64url>"}`. It is actually
`Content-Type: application/octet-stream` whose body is the encrypted request, MessagePack-framed —
it begins `84 A1 76 A5 "3.6.0" A1 72 D9 20 …`. POST those bytes to Coral unchanged; there is no
base64url decode step. Parsing it as JSON fails with `0xEF is an invalid start of a value`.

**Verified working as written:** §5.2 login and fragment parsing, §5.3 A–E including the `/f` body
with `timestamp: 0` / `requestId: ""` placeholders, §5.4 Friend/List, §5.5 decryption via
`/decrypt-response`, and the `shopUri` title-ID regex (real value:
`https://ec.nintendo.com/apps/04008e001f098000/CH?acdIndex=0`).

**Still unverified:** every Coral error status (9403/9404/9427/9437) — none has been provoked.

---

## 1. Goal

A small Windows app that **automatically** shows the game currently played on a **Nintendo Switch 2** (and Switch 1) as the user's Discord activity ("Playing …"). It should be:

- **Set up once, then it just works.** No manual game picking.
- **Light:** minimal RAM and CPU, because it runs in the background all day.
- **Usable two ways:** as a console app you start and watch, *and* windowless with autostart.
- **Possibly open source** later, so other users must be able to set it up with their own accounts.

---

## 2. Research summary: why this approach

### 2.1 How game detection works (the only practical way)

The Nintendo Switch Online smartphone app (internal name **"Coral"**, also "znca") talks to `api-lp1.znc.srv.nintendo.net`. Its friend list contains each friend's **presence**: online state, current game name and icon, and whether they're on Switch 1 or Switch 2.

**Nintendo's API does not return your own presence, only your friends'.** It was removed in 2022, see nxapi issue #1. Every automatic tool therefore uses a **second Nintendo Account** that is friends with the main account and reads the main account's presence from that friend list. **The user already has a second account.**

The main account must share its online status with that friend: on the Switch, System Settings → Users → user → friend settings. If sharing is limited to Best Friends, mark the second account as a Best Friend.

### 2.2 The hard part: request signing and encryption

The Coral API requires:
1. An **`f` token** that the real app generates natively (anti-abuse signature).
2. Since NSO app 3.x, **encrypted request and response bodies**.

Neither can be produced without running the real Android app. The community uses a hosted service, **nxapi-znca-api** (`https://nxapi-znca-api.fancy.org.uk/api/znca/`), run by the nxapi author. It provides:
- `POST /f`: generates `f` and returns the encrypted login request body.
- `POST /encrypt-request`: encrypts any Coral request body.
- `POST /decrypt-response`: decrypts any Coral response body.

Access needs a free **nxapi-auth client ID** (OAuth2 client-credentials), registered at `https://nxapi-auth.fancy.org.uk/oauth/clients` as a **Public** client with grant types **Client credentials** and **Refresh token**.

> Privacy consequence (must be in the README): the second account's Nintendo `id_token` and Coral access token pass through this third-party service. That's the reason to use a throwaway second account, never the main one.

### 2.3 Alternatives evaluated and rejected

| Option | Why rejected |
|---|---|
| **nxapi Electron app** (github.com/samuelthomas2774/nxapi) | Works, Switch 2 supported, but heavy (Electron). It's the **reference implementation** we port the protocol from. |
| **nxapi CLI** (`npm i -g nxapi`, `nxapi nso presence --friend-nsaid …`) | Works and has no Electron, but needs Node. The user wants their own small app. Still a useful fallback and comparison during development. |
| Third-party wrappers on nxapi (SalieriMZ/switch-1-2-discord-presence, Understudy) | Same backend; Understudy is Docker/server-side. Not what's wanted. |
| Manual pickers (dilanx/switchpresence, SwitchRPCUpdated, NS2-RPC, Rich Presence U, shizeh/switch-presence) | Manual game selection, so they're out. |
| On-console sysmodules (saekaze/switch-discord-activity, SwitchRPC by llsc12, SwitchPresence-Rewritten) | Need Atmosphère custom firmware. Not available on Switch 2, and modding risks a ban. |
| Capture-card detection (gituser6178/SwitchAutoPresence) | Not working yet ("still needs to be implemented and tested"), and needs extra hardware. |
| Reddit post r/Switch/1vgkuaa | Couldn't be read (Reddit is blocked for the research tools). Still unknown what it describes. |

### 2.4 Language choice

| Option | Idle RAM (rough estimate) | Notes |
|---|---|---|
| C# normal (framework-dependent) | ~25–40 MB | Best debugging |
| **C# NativeAOT** ← chosen | ~8–15 MB | No runtime needed; JSON needs source generation |
| Rust | ~2–5 MB | Smallest; new language to learn |
| Go | ~8–15 MB | |
| C++ | ~1–3 MB | Far more manual work for ~1–2 MB less than Rust |

CPU is negligible for all of them: three HTTPS calls per minute, then sleep. **Decision: C#, published as NativeAOT**, because it's the user's main stack with good debugging, and the RAM is acceptable. These numbers are estimates and should be measured early (see §9).

---

## 3. Decisions made

1. **C# / .NET 10, NativeAOT** release builds. Develop and debug as normal JIT; publish AOT.
2. **Windows only** for now. Discord runs on the user's Windows PC, and the app must run on the same machine because it uses Discord's local connection.
3. **Not a Windows Service.** Services run in session 0 and can't reliably reach the user's Discord pipe. Instead it's a normal per-user process.
4. **Two executables from one codebase:**
   - `switch-presence.exe`: console app (`OutputType=Exe`) with a live log; Ctrl+C clears the Discord activity and exits.
   - `switch-presence-bg.exe`: windowless (`OutputType=WinExe`); logs to a file.
5. **CLI commands:**
   - `switch-presence login`: one-time sign-in of the second account, then pick the friend (main account) from the friend list.
   - `switch-presence` (no args): run in the console.
   - `switch-presence autostart on|off`: write or remove the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value pointing at `switch-presence-bg.exe`. No admin needed.
   - Suggested extras: `status` (print current presence once, for debugging) and `logout`.
6. **Stored login is encrypted** with Windows DPAPI (CurrentUser scope), because the Nintendo session token gives full access to the second account.
7. **Data location:** `%APPDATA%\switch-presence\` for config and the encrypted token, `%LOCALAPPDATA%\switch-presence\logs\` for logs.
8. **Poll every 60 s by default** (configurable, minimum 30 s). nxapi uses about 1 min too.
9. **Re-implement the protocol ourselves** from the spec below. Don't copy nxapi code: nxapi is **AGPL-3.0-or-later**. Endpoint names and constants are facts and fine to use.

---

## 4. Architecture

```
SwitchPresence.sln
├─ src/SwitchPresence.Core/        (class library, AOT-compatible, all logic)
│   ├─ Nintendo/NintendoAccountClient.cs   session token, id_token, users/me
│   ├─ Nintendo/ZncaApiClient.cs           nxapi-auth token + /f, /encrypt-request, /decrypt-response
│   ├─ Nintendo/CoralClient.cs             Account/Login, Friend/List (encrypted)
│   ├─ Nintendo/RemoteConfig.cs            optional: current znca_version from nxapi's config
│   ├─ Discord/DiscordIpcClient.cs         named pipe, framing, handshake, SET_ACTIVITY
│   ├─ Presence/PresenceMapper.cs          Coral presence -> Discord activity
│   ├─ Presence/PresenceLoop.cs            poll loop, state machine, backoff
│   ├─ Storage/SecretStore.cs              DPAPI-encrypted session token
│   ├─ Storage/AppConfig.cs                JSON config
│   ├─ Autostart.cs                        HKCU Run key
│   └─ Json/AppJsonContext.cs              System.Text.Json source-gen context
├─ src/SwitchPresence.Console/     (Exe)    CLI parsing, console logging
└─ src/SwitchPresence.Background/  (WinExe) runs PresenceLoop, file logging, single-instance mutex
```

**Notes for NativeAOT:**
- Use `System.Text.Json` with a `JsonSerializerContext` (source generation). No reflection-based JSON.
- Use `HttpClient` with one shared `SocketsHttpHandler`.
- DPAPI: `System.Security.Cryptography.ProtectedData` (NuGet). Confirm it's AOT-clean; if not, P/Invoke `CryptProtectData` / `CryptUnprotectData` directly.
- Avoid heavy CLI frameworks. A hand-written argument parser is enough for 5 commands.
- Workstation, non-concurrent GC in the csproj (`<ServerGarbageCollector>false</ServerGarbageCollector>`, `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`). Consider `<InvariantGlobalization>true</InvariantGlobalization>` and `<UseSystemResourceKeys>true</UseSystemResourceKeys>` to cut size and RAM.
- Single instance: a named mutex (e.g. `Local\SwitchPresence`) so the console and background versions don't both run.

---

## 5. Protocol spec (ported from nxapi)

Source: nxapi `main` at commit `47c9d35d9d51d33e62dd6d3733b616e0ad31b02b` (2026-09-09). Files: `src/api/na.ts`, `src/api/coral.ts`, `src/api/f.ts`, `src/api/coral-types.ts`, `src/discord/*`. **This is read from source, not tested against live servers.** Verify each step during implementation (see §9).

### 5.1 Constants

| Name | Value |
|---|---|
| Coral client ID | `71b963c1b7b6d119` |
| Redirect URI | `npf71b963c1b7b6d119://auth` |
| NA scope | `openid user user.birthday user.screenName` |
| Coral base URL | `https://api-lp1.znc.srv.nintendo.net` |
| znca platform / platform version | `Android` / `12` |
| znca version (fallback) | `3.5.0` (nxapi hard-codes this; it changes when Nintendo updates the app) |
| Coral User-Agent | `com.nintendo.znca/{znca_version}(Android/12)` |
| f-API base URL | `https://nxapi-znca-api.fancy.org.uk/api/znca/` |
| f-API compatibility header | `X-znca-Client-Version: d8fAZDPzwimzQ7c6` |
| nxapi-auth scope | `ca:gf ca:er ca:dr` |
| nxapi remote config | `https://fancy.org.uk/api/nxapi/config` |

**Remote config (recommended, optional):** `GET https://fancy.org.uk/api/nxapi/config` with header `X-znca-Compatible-Version: 3.5.0`. In the JSON, `coral.znca_version` is the version to send, and `coral_auth.default` is `["nxapi", "<f-API URL>"]`. nxapi caches this for up to a day. This lets our app pick up Nintendo version bumps without a new release. Fall back to the constants if it fails. See open question Q4.

### 5.2 One-time login (`switch-presence login`)

1. Generate `state` = 36 random bytes, base64url, and `verifier` = 32 random bytes, base64url. `challenge` = base64url(SHA-256(verifier)).
2. Build the authorize URL:
   `https://accounts.nintendo.com/connect/1.0.0/authorize?` with query
   `state, redirect_uri=npf71b963c1b7b6d119://auth, client_id=71b963c1b7b6d119, scope=openid user user.birthday user.screenName, response_type=session_token_code, session_token_code_challenge=<challenge>, session_token_code_challenge_method=S256, theme=login_form`
3. Print the URL and optionally open it in the browser. The user signs in **with the second account**. On the "Linking an External Account" page they **right-click "Select this person" → copy link**. The link starts with `npf71b963c1b7b6d119://auth#…`.
4. Parse the **fragment** (after `#`) as query parameters. Check that `state` matches, check for an `error` parameter, then read `session_token_code`.
5. `POST https://accounts.nintendo.com/connect/1.0.0/api/session_token`
   - Headers: `Content-Type: application/x-www-form-urlencoded`, `Accept: application/json`, `User-Agent: NASDKAPI; Android`
   - Form body: `client_id`, `session_token_code`, `session_token_code_verifier=<verifier>`
   - Response: `{ "session_token": "…" }`. **This is the long-lived secret. Store it with DPAPI.**
6. Run the normal startup flow (§5.3), call Friend/List (§5.4), show the friends with their names, and let the user pick the main account. Store its **`nsaId`** in the config.

### 5.3 Startup / re-login flow

**A. Nintendo Account token** (short-lived, ~15 min; get a fresh one for each Coral login)
`POST https://accounts.nintendo.com/connect/1.0.0/api/token`
- Headers: `Content-Type: application/json`, `Accept: application/json`, `User-Agent: Dalvik/2.1.0 (Linux; U; Android 8.0.0)`
- Body: `{"client_id":"71b963c1b7b6d119","session_token":"<session_token>","grant_type":"urn:ietf:params:oauth:grant-type:jwt-bearer-session-token"}`
- Response: `access_token`, `id_token`, … Errors arrive as `{error, error_description}` or `{errorCode, detail}`.

**B. NA user info** (needed for `id` and `language`)
`GET https://api.accounts.nintendo.com/2.0.0/users/me`
- Headers: `Authorization: Bearer <access_token>`, `Accept-Language: en-GB`, `User-Agent: NASDKAPI; Android`, `Accept: application/json`
- Response includes `id` (NA ID) and `language`.

**C. nxapi-auth access token** (cache until `expires_in`; refresh with `refresh_token` if one was returned)
1. Discovery: `GET https://nxapi-znca-api.fancy.org.uk/.well-known/oauth-protected-resource` → `authorization_servers[0]`.
2. `GET <issuer>/.well-known/oauth-authorization-server` → `token_endpoint`. These can be cached.
3. `POST <token_endpoint>` as form-urlencoded:
   - First time: `grant_type=client_credentials&client_id=<our client id>&scope=ca:gf ca:er ca:dr`
   - Refresh: `grant_type=refresh_token&refresh_token=…&client_id=…`. On `invalid_grant`, fall back to client credentials.
   - Response: `access_token`, `expires_in`, optional `refresh_token`, `scope`.

All f-API calls use these headers: `X-znca-Client-Version: d8fAZDPzwimzQ7c6`, `X-znca-Platform: Android`, `X-znca-Version: <znca_version>`, `Authorization: Bearer <nxapi-auth token>`, `Content-Type: application/json`, and a descriptive `User-Agent` (e.g. `switch-presence/0.1 (+repo url)`). If an f-API call returns error `invalid_token`, drop the cached token and retry once.

**D. f token plus encrypted login body**
`POST {f-API}/f`, body:
```json
{
  "hash_method": "1",
  "token": "<NA id_token>",
  "na_id": "<NA user id>",
  "encrypt_token_request": {
    "url": "https://api-lp1.znc.srv.nintendo.net/v4/Account/Login",
    "parameter": { "naIdToken": "<NA id_token>", "language": "<user.language>", "timestamp": 0, "requestId": "", "f": "" }
  }
}
```
Response: `{ "f", "timestamp", "request_id", "encrypted_token_request": "<base64>", "warnings"? }`. The service fills `f`, `timestamp` and `requestId` into the parameter and returns the encrypted body. Log any `warnings`.

**E. Coral login**
`POST https://api-lp1.znc.srv.nintendo.net/v4/Account/Login`
- Headers: `X-Platform: Android`, `X-ProductVersion: <znca_version>`, `Content-Type: application/octet-stream`, `Accept: application/octet-stream,application/json`, `User-Agent: com.nintendo.znca/<znca_version>(Android/12)`
- Body: the raw bytes of base64-decoded `encrypted_token_request`
- Response: encrypted bytes. Decrypt them (§5.5), then parse `{status, result:{user:{id, nsaId, name,…}, webApiServerCredential:{accessToken, expiresIn}}, correlationId}`.
- `status` must be `0`. Keep `accessToken` (the Coral bearer token, ~2 h) and compute its expiry from `expiresIn`.

*(nxapi renews with `/v4/Account/GetToken` via `naIdToken` plus `coral_user_id` instead of a full re-login. For simplicity, v1 can just redo A → E when the token expires. See Q6.)*

### 5.4 Polling: Friend/List

Every poll:
1. Plain body: `{"parameter":{}}`
2. Encrypt: `POST {f-API}/encrypt-request` with `{"url":"https://api-lp1.znc.srv.nintendo.net/v4/Friend/List","token":"<coral accessToken>","data":"{\"parameter\":{}}"}`. The response is `{ "data": "<base64url>", "warnings"? }`. Note: **base64url**.
3. `POST https://api-lp1.znc.srv.nintendo.net/v4/Friend/List`
   - Headers: `Content-Type: application/octet-stream`, `Accept: application/octet-stream,application/json`, `Accept-Language: en-GB`, `User-Agent: com.nintendo.znca/<v>(Android/12)`, `Authorization: Bearer <coral accessToken>`, `X-Platform: Android`, `Pragma: no-cache`, `Cache-Control: no-cache`
   - Body: the decoded bytes.
4. Decrypt the response (§5.5), parse it, check `status`:
   - `0`: OK.
   - `9404` (TOKEN_EXPIRED) or `9403` (INVALID_TOKEN): re-login (A → E), then retry once.
   - `9427` (UPGRADE_REQUIRED): the znca version is outdated. Re-fetch the remote config; if that doesn't help, log a clear "update needed" error and back off.
   - `9437` (RATE_LIMIT_EXCEEDED): back off.
5. Find the friend whose `nsaId` matches the configured one.

**Response shape** (`result`): `{ friends: Friend[], extractFriendsIds: string[] }`

```
Friend { id, nsaId, name, imageUri, image2Uri, isFriend, isFavoriteFriend, …,
         presence: { state, updatedAt, logoutAt, platform?, game: {} | PresenceGame } }
state: "OFFLINE" | "INACTIVE" | "ONLINE" | "PLAYING"
   INACTIVE = console online but not in a game; ONLINE = in a game; PLAYING = in a game and playing online
platform: 1 = Switch (NX), 2 = Switch 2 (OUNCE)
PresenceGame { name, imageUri, shopUri, totalPlayTime (minutes), firstPlayedAt, sysDescription }
```
Presence updates on Nintendo's side lag by about a minute.

### 5.5 Response decryption

`POST {f-API}/decrypt-response` with body `{"data":"<standard base64 of the raw response bytes>"}` and header `Accept: text/plain`. The response body is the plain JSON text.

Note from nxapi's code: Coral sends encrypted responses with `Content-Type: application/json`, even though they're binary. If the response status isn't 2xx, still try decrypting; if that fails, try parsing it as plain JSON for the error.

**Cost per poll:** 3 HTTPS calls (encrypt → Coral → decrypt).

---

## 6. Discord side

### 6.1 Local IPC (no library needed)

- Connect to the named pipe `\\.\pipe\discord-ipc-0`; if that fails, try `-1` … `-9`. Use `NamedPipeClientStream(".", "discord-ipc-N", PipeDirection.InOut)`.
- Frame format: `int32 LE opcode`, `int32 LE payload length`, then UTF-8 JSON.
  - Opcodes: `0` HANDSHAKE, `1` FRAME, `2` CLOSE, `3` PING, `4` PONG.
- Handshake: send op 0 `{"v":1,"client_id":"<discord app id>"}` and wait for op 1 with `"evt":"READY"`.
- Set the activity: op 1
  ```json
  {"cmd":"SET_ACTIVITY","nonce":"<guid>","args":{"pid":<our pid>,"activity":{ … }}}
  ```
- Clear it: the same with `"activity": null`.
- Answer PING with PONG. If the pipe breaks (Discord closed), reconnect with backoff and send the current activity again.
- **The client ID decides the big "Playing <app name>" label.** Switching between the Switch 1 and Switch 2 app means closing and redoing the handshake with the other ID.

### 6.2 Activity mapping (mirrors nxapi's behavior)

| Coral | Discord activity |
|---|---|
| `state` OFFLINE / INACTIVE | Clear the activity |
| `platform` 2 | Client ID of a "Nintendo Switch 2" Discord app |
| `platform` 1 or missing | Client ID of a "Nintendo Switch" Discord app |
| `game.name` | `details` (≤128 chars) |
| `sysDescription`, else "Playing online" if PLAYING, else omitted | `state` |
| `game.imageUri` (an https URL; Discord accepts URLs) | `assets.large_image`; `large_text` like "Playing on Nintendo Switch 2" |
| — | `type: 0` (Playing), `status_display_type: 2` (shows the game name instead of the app name in the member list) |
| First time this game was seen, in local time | `timestamps.start`. Reset only when the game changes, not on every poll. |
| `shopUri` | Optional button "Nintendo eShop". The title ID is `^https://ec\.nintendo\.com/apps/([0-9a-f]{16})/` |

nxapi's Discord app IDs, for reference: Switch `950883021165330493`, Switch 2 `1358060657957928970`. See Q2 on whether to reuse them.

Only send SET_ACTIVITY when the mapped activity actually changed.

---

## 7. Config and storage

`%APPDATA%\switch-presence\config.json`:
```json
{
  "friendNsaId": "…",
  "friendName": "…",
  "pollIntervalSeconds": 60,
  "showPlayingOnline": true,
  "showEshopButton": true,
  "discordClientIdSwitch": "…",
  "discordClientIdSwitch2": "…",
  "nxapiAuthClientId": null,
  "zncaVersionOverride": null
}
```
- `nxapiAuthClientId`: `null` means use the built-in project ID (see Q1).
- `%APPDATA%\switch-presence\session.bin`: the DPAPI-encrypted `session_token`.
- Optionally cache the nxapi-auth token and the Coral token (also DPAPI) so restarts don't trigger a full login each time.

---

## 8. Runtime behavior

The loop runs as a state machine:
```
Start → load config + secret → (no secret? tell user to run `login`, exit)
     → get remote config (optional) → Coral login → loop:
        poll → map → if changed: Discord set/clear → sleep(interval)
```
- **Backoff** on network or service errors: 30 s, 1 min, 2 min, 5 min, max 10 min. Keep the last activity in Discord during short outages, but clear it after ~10 min with no data.
- **Discord not running:** keep polling cheaply, or pause polling until the pipe exists. Pausing saves requests; v1 can pause (see Q7).
- **Sleep/resume:** a large time jump means the PC was asleep, so force a token check.
- **Console version:** a one-line log per change, e.g. `12:03 Mario Kart World (Switch 2, online)`. On Ctrl+C, clear the activity, close the pipe and exit.
- **Background version:** log to file, rotated (e.g. keep 3 × 1 MB). Exit cleanly on the Windows logoff or shutdown message.

---

## 9. Implementation plan (milestones)

1. **Spike against live servers (do this first; it carries the most risk).** A throwaway console program running §5.2 → §5.4 with debug output:
   - Register the nxapi-auth client ID (Q1).
   - Log in with the second account, list friends, print the main account's presence while playing something on the Switch 2.
   - Compare with `nxapi nso presence` output if anything looks off.
2. **Discord IPC:** set a hard-coded activity, check it in Discord, clear it, reconnect after restarting Discord.
3. Core library with clean classes (§4), JSON source generation, DPAPI storage, config.
4. Console app: `login`, run, `status`, `logout`.
5. Background app, single-instance mutex, file logging, `autostart on|off`.
6. **NativeAOT publish** for win-x64. Fix trimming and AOT warnings. **Measure idle RAM** (Task Manager working set after 10 min) for AOT vs JIT and record it in the README.
7. README: setup guide (second account, friend sharing settings, login steps), the privacy note about the third-party service, troubleshooting, license.
8. Optional: tray icon (Q3), GitHub Actions release build.

**Tests worth writing:** fragment parsing of the `npf…` link, presence-to-activity mapping (each state and platform), eShop title ID regex, IPC frame encode/decode, backoff timing. The Nintendo calls can't be tested offline, so keep them behind an interface and use recorded JSON fixtures.

---

## 10. Open questions

| # | Question | Notes / suggestion |
|---|---|---|
| Q1 | **nxapi-auth client ID:** one built-in project ID, or each user registers their own? | Easiest for users: the project registers one Public client and ships it, with a config override. Check nxapi-auth's terms for shared public clients. Understudy makes each user register. |
| Q2 | **Discord app IDs:** reuse nxapi's, or register our own "Nintendo Switch" and "Nintendo Switch 2" apps? | For an open-source project, register our own (free, 2 min in the Developer Portal; the app name is what Discord shows). Using Nintendo's name or logo as app name and icon is a trademark gray area, same as every existing tool. |
| Q3 | **Tray icon** in v1? | WinForms `NotifyIcon` isn't NativeAOT-friendly. It would need a raw Win32 `Shell_NotifyIcon` + message loop via P/Invoke (~150 lines), which adds some RAM. Without it, the windowless version can only be stopped via Task Manager or `autostart off` plus a reboot. Alternative: a `switch-presence stop` command that signals the background process through a named event. |
| Q4 | **Depend on nxapi's remote config** for the znca version and f-API URL? | Saves releases when Nintendo bumps the app version. Downside: an extra dependency on nxapi's infrastructure. Suggest: use it with the hard-coded fallback, plus a config override. |
| Q5 | **License** of our project? | We don't copy nxapi code, so MIT or GPL are both possible. Decide before publishing. |
| Q6 | **Token renewal:** full re-login vs `/v4/Account/GetToken`? | v1: full re-login every ~2 h (one extra `/f` call). Optimize later if needed. |
| Q7 | **Poll when Discord is closed?** | Suggest pausing Nintendo polling while no Discord pipe exists. Fewer requests and less load on the free f-API. |
| Q8 | **Show "INACTIVE"** (console on, no game) as something like "On the HOME Menu"? | Default: clear the activity. |
| Q9 | **What did the Reddit post describe?** | Unread. If the user pastes it, check whether it shows a better approach. |
| Q10 | **Ban risk** for the second account | No known bans for presence-only use via nxapi in years, but it's unofficial API use. Accept it, document it, and use a throwaway account. |

---

## 11. Risks

- **Nintendo changes** (new app version, API version bump, encryption changes): the app breaks until the constants or protocol are updated. Mitigation: remote config (Q4) and clear error messages for status 9427.
- **nxapi-znca-api is a free community service** with no guarantee. If it goes away, so does the app, unless someone runs their own (nxapi-znca-api is self-hostable, but that needs a rooted Android device or emulator).
- **Rate limits:** stay at ≥60 s polling and pause when Discord is closed.

---

## 12. References

- nxapi (reference implementation, AGPL-3.0): https://github.com/samuelthomas2774/nxapi, commit `47c9d35d` (2026-09-09)
  - `src/api/na.ts`: Nintendo Account login
  - `src/api/coral.ts`: Coral endpoints, headers, encryption flow
  - `src/api/f.ts`: nxapi-znca-api client and nxapi-auth OAuth
  - `src/api/coral-types.ts`: response types (Friend, Presence, PresencePlatform)
  - `src/discord/util.ts`, `src/discord/titles.ts`, `src/discord/rpc.ts`: activity mapping and app IDs
  - Issue #1: why a second account is needed
- Understudy (nxapi-auth registration steps, Switch 2 confirmed): https://github.com/the-snesler/understudy
- SalieriMZ/switch-1-2-discord-presence (Windows tray app on nxapi, Switch 1 and 2): https://github.com/SalieriMZ/switch-1-2-discord-presence
- nxapi-auth client registration: https://nxapi-auth.fancy.org.uk/oauth/clients
- Discord Rich Presence docs: https://docs.discord.com/developers/rich-presence/using-with-the-discord-social-sdk

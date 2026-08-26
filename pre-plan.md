## Deltanium PRE (Proxy Re‑Encryption) Plan — Tag "followers:<YYYY>"

### 0) Scope and Goals
- Keep Option 1 (current): per‑recipient encryptedKey; content blocks uploaded once, metadata per recipient.
- Add Option 2 (PRE‑tag): author encrypts once under policy tag "followers:<YYYY>" using author key; followers obtain access via re‑encryption bound to that tag. Epoch is the current year.
- Client and store should remain zero‑knowledge about plaintext; central/store must not learn symmetric keys.

### 1) Cryptographic Model (Option 2)
- Entities/keys
  - Author A: long‑term keypair (sk_A, pk_A) already exists in app.
  - Follower B: keypair (sk_B, pk_B) already exists in app.
  - Symmetric post key K: random 256‑bit (AES‑256) per post.
  - Policy tag t = "followers:<YYYY>". Example for 2025: t = "followers:2025".
  - PRE scheme with tag/policy support (Umbral‑style, CPRE). Notation:
    - Capsule C_A^t(K): encapsulation of K to pk_A with tag t.
    - Re‑encryption key rk_{A→B}^t: generated using sk_A and pk_B, bound to tag t (and optionally a context like fileId).

- Posting (author A)
  1. Generate K, encrypt content blocks: Enc_K(content).
  2. Encapsulate K into PRE bundle B_A^t(K): includes [capsule, ciphertext(K), pk_A]. Note: rust-umbral currently does not bind "tag" at the crypto layer; tag/epoch will be enforced at the policy/token layer.
  3. Store base64(PRE bundle) in metadata `encryptedKey` (along with the tag fields below).

- Reading (follower B)
  - Proxy flow (recommended):
    1. B proves possession of sk_B (PoP: sign nonce from central or store).
    2. Store obtains permission (token/rk) from Central for (A,B,t), then re‑encrypts:
       C_A^t(K) --rk_{A→B}^t--> C_B(K). Store returns `encryptedKeyForB`.
    3. B uses sk_B to decapsulate C_B(K) → K, then decrypts content.
  - Client flow (simple, less safe): Central releases rk_{A→B}^t to B after PoP; B transforms locally.

- Revocation/epoch
  - Epoch set to current year; each year tag changes (followers:<YYYY>). Followers only receive rk for epochs they are entitled to.
  - Unfollow: stop issuing rk for new epochs. Old epochs may remain readable unless content is rotated.

### 2) Data Model Changes

#### 2.1 App Post Metadata (when Option 2 is selected)
- Existing fields retained; add and reinterpret as follows:
- `encryptedKey` (string, base64): holds PRE bundle B_A^t(K) consisting of [capsule, ciphertext(K), authorPublicKey].
  - `capsuleFor` (string): "tag".
  - `policyTag` (string): e.g., "followers:2025".
  - `policyScheme` (string): "CPRE".

Example snippet stored in metadata JSON (contains PRE bundle base64 in `encryptedKey`):
```json
{
  "type": "post",
  "shareType": "followers",
  "encryptedType": "encrypted",
  "encryptedKey": "<base64 PRE bundle B_A^t(K)>",
  "capsuleFor": "tag",
  "policyTag": "followers:2025",
  "policyScheme": "CPRE"
}
```

#### 2.2 Store FileMetadata additions
- Add optional fields to `FileMetadata`:
  - `PolicyTag` (string?)
  - `CapsuleFor` (string?)
  - `PolicyScheme` (string?)
- `EncryptedKey` continues to exist and will store the capsule for Option 2.

#### 2.3 Central user_follows schema additions
- Extend follow record with policy re‑keys per epoch (if storing persistently):
```json
{
  "FollowerPublicKey": "<pk_B>",
  "FollowingPublicKey": "<pk_A>",
  "policyKeys": [
    { "tag": "followers:2025", "rk": "<base64 rk_{A→B}^t>", "scope": "tag", "createdAt": "<ISO>" }
  ]
}
```
- Alternatively: do not persist rk; generate/serve a short‑lived transform token upon PoP.

### 3) APIs

#### 3.1 Central API (new)
- POST `/api/policy/publish-tag`
  - Auth: A (signed). Body: { tag: "followers:2025", policyScheme: "CPRE" }
  - Registers intent to use this tag.

- POST `/api/policy/upload-rekey`
  - Auth: A. Body: { followerPubKey: pk_B, tag: "followers:2025", rk: base64, scope?: "tag" | "file", fileId?: string }
  - Stores rk or issues a transform authorization for store‑side re‑encrypt.

- GET `/api/policy/nonce`
  - Returns nonce for PoP (used by B).

- POST `/api/policy/fetch-rekey`
  - Auth: B. Body: { followingPubKey: pk_A, tag: "followers:2025", proof: { nonce, signature }, mode?: "client"|"proxy" }
  - Verifies follow and entitlement (epoch/year). If `mode=client`, returns rk (short‑lived). If `mode=proxy`, returns a short‑lived transform token for Store.

#### 3.2 Store API (new)
- POST `/api/pre/reencrypt`
  - Headers: X-User-PubKey = pk_B, X-Timestamp, X-Signature (existing signing pattern)
  - Body: { fileId: string, policyTag: string, token?: string }
  - Steps:
    1) Load metadata; verify `CapsuleFor=="tag"` and `PolicyTag` matches the request.
    2) Validate entitlement via Central (use `token` or call Central with B's PoP to obtain a short-lived token/rk). Enforce tag/epoch at the policy layer.
    3) Transform PRE bundle: B_A^t(K) → B_B(K) (append verifying_pk + cfrag after re-encrypt). Do not decrypt plaintext.
    4) Return { encryptedKeyForB: base64(transformed PRE bundle) }.

#### 3.3 Store feeds (existing)
- `/api/post/followers-feed`: include posts with `shareType = "followers"` regardless of presence of `RecipientPubKey`. Returns both Option 1 and Option 2 posts; client chooses decrypt path.

### 4) App Changes (Flutter)

#### 4.1 UI Toggle
- In create post (followers), add mode selector:
  - Option 1: "Direct share (per‑recipient)" (current behavior)
  - Option 2: "PRE by tag (followers:<YYYY>)"
  - Default Option 2 for new followers posts.

#### 4.2 Post creation (Option 2)
- Build `policyTag = "followers:" + currentYear`.
- Generate K; encrypt content blocks as today.
- Call PreService.createTaggedCapsule(K, pk_A, policyTag) → base64 capsule.
- In metadata: set `capsuleFor="tag"`, `policyTag`, `policyScheme="CPRE"`, and set `encryptedKey` to capsule.
- Upload metadata and blocks as usual.

#### 4.3 Reading (followers feed)
- When `capsuleFor != "tag"`: use existing per‑recipient flow (Option 1).
- When `capsuleFor == "tag"`:
  - Proxy flow (preferred): call Store `/api/pre/reencrypt` with (fileId, policyTag). Receive `encryptedKeyForB` (transformed PRE bundle); decapsulate with sk_B → K; decrypt blocks.
  - Client flow (fallback): call Central `/api/policy/fetch-rekey` with PoP; receive rk (short-lived scope); transform bundle locally; decapsulate → K; decrypt blocks.

#### 4.4 New App Services
- PreService (new facade in app):
  - `String buildPolicyTagForYear(int year)` → "followers:<YYYY>".
  - `Uint8List createTaggedCapsule(Uint8List K, String pkAuthor, String tag)` → capsule.
  - `Uint8List transformCapsuleForRecipient(Uint8List capsule, Uint8List rk)` → transformed capsule (client flow only).
  - Internals may call FFI/WASM/gRPC to a PRE engine (e.g., Umbral binding).

### 5) Algorithms (high‑level)

#### 5.1 Create post (Option 2)
1) K ← random(32 bytes)
2) blocks ← EncryptAndSplit(content, K)
3) t ← "followers:" + Year(now)
4) bundle ← EncapsulateWithTag(pk_A, t, K) → PRE bundle B_A^t(K) = [capsule,ciphertext(K),pk_A]
5) metadata.encryptedKey ← base64(bundle)
6) metadata.capsuleFor ← "tag"; metadata.policyTag ← t; metadata.policyScheme ← "CPRE"
7) Upload metadata and blocks (same as the current pipeline).

#### 5.2 Follow event (A→B)
1) On A's device: rk ← ReKey(sk_A, pk_B, tag=t[, context])
2) Central: store rk (or issue transform token policy) bound to (A,B,t[, context]).

#### 5.3 Read post (B)
Proxy (preferred):
1) B fetches the feed; sees tag t in metadata.
2) B PoP (signs nonce). Store authenticates via Central, transforms PRE bundle: B_A^t(K) → B_B(K) (append verifying_pk + cfrag).
3) B decapsulates with sk_B → K; decrypts blocks.

Client (fallback):
1) B obtains rk from Central after PoP (short-lived rk, scoped by tag[,fileId]).
2) B transforms PRE bundle locally → B_B(K); decapsulate → K; decrypt blocks.

### 6) Security & Privacy
- Proof‑of‑possession (PoP) for B before re‑encrypt or issuing rk (nonce challenge, signed with sk_B).
- Tag/Epoch binding: rust-umbral does not bind tag at the crypto layer; enforce via Central token/rk-scope (must contain {A,B,policyTag[,fileId]}, short TTL, Central signature). Store only transforms when the token is valid.
- Authorization: Central checks follow + entitlement for the epoch (followYear(B→A) ≤ epoch).
- Audit: Central/Store log (A,B,fileId,tag,timestamp,mode) on every transform.
- Prefer the proxy flow to avoid releasing rk to the client; if client flow is used, rk must be short-lived and tightly scoped (tag[,fileId]).

### 7) Backward Compatibility
- Option 1 unchanged. Clients can read both types in feeds.
- Mixed deployment supported: new posts can use Option 2 while old posts stay Option 1.

### 8) Rollout Plan
1) Central API: add policy endpoints + PoP nonce; update follow schema (or implement transform tokens instead of persistent rk).
2) Store: add FileMetadata fields; add `/api/pre/reencrypt`; update followers‑feed to include tag‑based posts.
3) App: add UI toggle; implement Option 2 in PostService + PreService; implement read path for tag posts.
4) Security hardening: rate limits, audit, rotate epoch yearly.
5) Optional: later migrate followers posts to Option 2 by re‑publishing keys.

### 9) Open Questions / Decisions
- PRE engine choice and binding (Rust/Go library via FFI/WASM/gRPC). Prototype with client flow first, then move to proxy.
- Scope of rk: tag‑only vs tag+fileId; recommended: tag‑only with yearly epoch for UX, or tag+fileId for stricter control.
- Storage of rk: persist in Central vs ephemeral transform token.

### 10) Node‑local PRE integration (no shared PRE service)

- Requirement: App, Central API, and Store are independent, distributed nodes. Each node integrates its own local PRE. Do not deploy a shared PRE proxy/service.

- Library choice
  - Prefer Rust (Umbral/CPRE implementation) built as:
    - Mobile/Desktop: `.so` (Android, Linux), `.dylib` (macOS/iOS), `.dll` (Windows)
    - Web: WASM + glue JS
  - Expose a minimal C/ABI so hosts (Dart/Flutter, .NET) can call via FFI/PInvoke/WASM.

- Required PRE lib functions (identical on every node)
  - `encapsulate_with_tag(pk_author, tag, K) -> PRE bundle [capsule,ciphertext(K),pkA]`
  - `generate_rekey(sk_author, pk_recipient, tag[, scope]) -> rk_A_to_B_tag`
  - `reencrypt(bundle, rk_A_to_B_tag) -> bundle_for_B (append verifying_pk + cfrag)`
  - `decapsulate(sk_recipient, bundle_for_B) -> K`
  - All buffers must be zeroized after use; API returns standard error codes.

- Per-node integration
  - App (Flutter)
    - Native (iOS/Android/macOS/Windows/Linux): use `dart:ffi` to call the Rust lib.
    - Web: load WASM; wrap with `package:js`/`dart:js_interop`.
    - Used in `PreService`:
      - `createTaggedCapsule(K, pkA, tag)` calls `encapsulate_with_tag`.
      - `generateRekey(skA, pkB, tag)` calls `generate_rekey` (when there is a new follower / start of year).
      - Client fallback: `transformCapsule(capsule, rk)` calls `reencrypt` if client mode is selected.
    - Security:
      - sk_A/sk_B derived from mnemonic; keep in memory as briefly as possible, never log; zeroize after use.
      - rk is generated only when needed, uploaded to Central; do not persist rk long-term.
    - Periodic tasks:
      - Sync new followers → generate rk_{A→B}^{tag} and upload to Central.
      - Year-start rollover → generate rk for the new tag.

  - Store node (C#)
    - Integrate the Rust lib via P/Invoke (`DllImport`) or a .NET wrapper (if available).
    - Endpoint `/api/pre/reencrypt`:
      - Authenticate the request (headers); verify Central token for (A,B,tag[,fileId]).
      - Call `reencrypt(bundle, rk|token)` from the lib/policy layer → return `EncryptedKeyForB` (transformed PRE bundle).
      - Do not hold sk_A/sk_B; rk is used only temporarily; zeroize buffers; rate‑limit; audit.
    - No shared PRE service: each Store runs its own PRE lib, independently updated/deployed.

  - Central API (C#)
    - Does not perform re‑encrypt; only verifies PoP, checks entitlement/epoch, stores rk (or issues a short-lived token so Store can obtain rk).
    - Integrating the PRE lib is not required (Central does not transform); it only manages rk/token.
    - Policy:
      - PoP (B signs nonce) before issuing rk/token.
      - Entitlement: only issue rk for `followers:<YYYY>` where `YYYY >= followYear(B→A)`.
      - Prefer tokens for Store (proxy mode); only return rk to the client on fallback.

- End-to-end flow (no shared PRE service)
  1) App A publishes a followers post (Option 2): create K; `encapsulate_with_tag(pkA, tag, K)` → `encryptedKey = capsule`.
  2) A tracks followers and epoch:
     - `generate_rekey(skA, pkB, tag)` for each entitled B → `upload-rekey` to Central.
  3) App B reads the post:
     - PoP with Central → receive token (proxy mode) or rk (client mode fallback).
     - Proxy mode: B calls Store `/api/pre/reencrypt` (Store checks token, loads rk if needed) → Store calls local lib `reencrypt(capsule, rk)` → returns `C_B(K)`.
     - Client mode: B receives rk, calls local lib `reencrypt(capsule, rk)` → `C_B(K)`.
     - B calls `decapsulate(skB, C_B(K))` → obtains K, decrypts content.

- Build & release (cross-platform)
  - Rust lib: build matrix (Android/arm64‑v8a, iOS/arm64, macOS/x86_64+arm64, Windows/x64, Linux/x64) + WASM.
  - App: package artifacts per platform; dynamic loading via `dart:ffi`/WASM loader.
  - Store/Central: include native lib in publish; configure path to load DLL/DYLIB/SO.
  - Versioning: same PRE lib version across nodes to avoid capsule/rk format mismatches.

-- Data format
  - `encryptedKey` (PRE bundle) = base64 of a length-prefixed PRE blob:
    - [u32 cap_len][cap]
    - [u32 ct_len][ciphertext_of_K]
    - [u32 pkA_len][pkA]
    - After re-encrypt append:
      - [u32 vpk_len][verifying_pk]
      - [u32 cfrag_len][cfrag]
  - `rk` = base64; scoped by `tag` (and optionally `fileId`) managed by Central.
  - Proxy `token` (JSON, signed by Central): { followingPubKey, followerPublicKey, tag[, fileId], exp, signature } allowing Store to transform.

- Testing & safety
  - TEST vectors: round‑trip encapsulate → reencrypt → decapsulate.
  - Negative: wrong tag, wrong epoch, wrong PoP, wrong rk → reject.
  - Bench: transform latency; memory zeroization; fuzz inputs (capsule/rk) against the Rust lib.


### 11) Implementation status & notes (CURRENT)

- App (Flutter)
  - UI: Followers Option 1/Option 2 toggle has been added in create post.
  - Create post (Option 2):
    - Generate K with a secure RNG; `PostService.createPost` calls FFI `encapsulate_with_tag` via `PreFfi` to create the PRE bundle and injects it into `FileCryptoService.encryptAndSplitFile` via `injectedSymmetricKey` and `preEncryptedKeyBundle`.
    - Metadata records `encryptedKey` = PRE bundle (base64), `capsuleFor="tag"`, `policyTag`, `policyScheme="CPRE"`.
  - Read post (Followers feed):
    - If `capsuleFor!='tag'` → decrypt via Option 1.
    - If `capsuleFor=='tag'` → call Store `/api/file/pre/reencrypt` (proxy mode) to receive the transformed bundle; `PreService.decapsulateAndRecoverKey` calls FFI `decapsulate_for_recipient` to obtain K and decrypt content.
    - Client mode fallback exists: `PreService.clientTransform` (PoP → Central `/policy/fetch-rekey?mode=client` → FFI `reencrypt`).
  - Rekey (author):
    - `PreService.publishTag`, `uploadRekey`, `generateAndUploadRekeys` (generate rk via FFI `generate_rekey` and upload to Central) are implemented; currently called automatically after publishing a Followers+PRE post. Can also be called on startup/sync by epoch.
  - FFI/Loader:
    - `lib/services/pre_ffi.dart` wraps C‑ABI: `umbral_encapsulate_with_tag`, `umbral_generate_rekey`, `umbral_reencrypt`, `umbral_decapsulate_for_recipient`, `umbral_free`.
    - macOS: loader tries `libumbral_pre.dylib` and falls back to `@executable_path/../Frameworks/libumbral_pre.dylib`.

- Store
  - `/api/file/pre/reencrypt` validates the Central token and calls core Umbral PRE to transform; returns `encryptedKeyForB`.
  - `followers-feed` returns both Option 1/2 + PRE fields.

- Central API
  - `PolicyController`: `nonce`, `publish-tag`, `upload-rekey`, `fetch-rekey`, `validate-token` are complete; token scoped by tag (and optionally fileId), short TTL.

- Bundle format
  - As described in the "Data format" section (length‑prefixed). App has a helper to extract `pkA` from the bundle for client‑transform.


### 12) Build & packaging (Flutter CURRENT)

- Script: `deltanium-core/scripts/build_umbral_pre_flutter.sh`
  - Android: `--android --app-dir <path-to-app>` → copy `.so` into `android/app/src/main/jniLibs/{arm64-v8a,armeabi-v7a,x86_64}`.
  - macOS: `--macos --app-dir <path-to-app>` → copy `.dylib` into `macos/Frameworks`. Recommended to add a Copy Files phase to bundle it in the app.
  - iOS: `--ios` (cargo-lipo if available) → produce a universal static lib; must add it to Runner and link manually.

- macOS loader: fallback to `@executable_path/../Frameworks/libumbral_pre.dylib` is implemented for easier development.

- Remaining work (does not block macOS development):
  - Add a Copy Files phase for macOS to bundle `.dylib` in release.
  - Finish iOS integration (add static lib/xcframework to Runner) and Android (ensure jniLibs includes all required ABIs).
  - (Optional) UI toggle for proxy/client transform.
  - (Optional) Automatically trigger generate/upload rk on app startup or when a new follower appears / epoch changes.

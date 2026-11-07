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
  2. Encapsulate K thành PRE bundle B_A^t(K): gồm [capsule, ciphertext(K), pk_A]. Lưu ý: rust-umbral hiện không bind "tag" ở tầng crypto; tag/epoch sẽ được cưỡng chế ở tầng policy/token.
  3. Lưu base64(PRE bundle) vào metadata `encryptedKey` (kèm các tag fields bên dưới).

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
- `encryptedKey` (string, base64): mang PRE bundle B_A^t(K) gồm [capsule, ciphertext(K), authorPublicKey].
  - `capsuleFor` (string): "tag".
  - `policyTag` (string): e.g., "followers:2025".
  - `policyScheme` (string): "CPRE".

Example snippet stored in metadata JSON (chứa PRE bundle base64 trong `encryptedKey`):
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
    1) Load metadata; verify `CapsuleFor=="tag"` và `PolicyTag` trùng request.
    2) Validate entitlement qua Central (dùng `token` hoặc tự gọi Central với PoP của B để lấy token/rk tạm thời). Cưỡng chế tag/epoch ở tầng policy.
    3) Transform PRE bundle: B_A^t(K) → B_B(K) (append verifying_pk + cfrag sau khi re-encrypt). Không giải plaintext.
    4) Return { encryptedKeyForB: base64(PRE bundle đã transform) }.

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
  - Proxy flow (preferred): gọi Store `/api/pre/reencrypt` với (fileId, policyTag). Nhận `encryptedKeyForB` (PRE bundle đã transform); decapsulate với sk_B → K; decrypt blocks.
  - Client flow (fallback): gọi Central `/api/policy/fetch-rekey` với PoP; nhận rk (scope ngắn hạn); transform bundle local; decapsulate → K; decrypt blocks.

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
7) Upload metadata and blocks (như pipeline hiện tại).

#### 5.2 Follow event (A→B)
1) On A's device: rk ← ReKey(sk_A, pk_B, tag=t[, context])
2) Central: store rk (or issue transform token policy) bound to (A,B,t[, context]).

#### 5.3 Read post (B)
Proxy (preferred):
1) B lấy feed; thấy tag t trong metadata.
2) B PoP (ký nonce). Store xác thực qua Central, transform PRE bundle: B_A^t(K) → B_B(K) (append verifying_pk + cfrag).
3) B decapsulates với sk_B → K; decrypt blocks.

Client (fallback):
1) B lấy rk từ Central sau PoP (rk ngắn hạn, scope theo tag[,fileId]).
2) B transform PRE bundle local → B_B(K); decapsulate → K; decrypt blocks.

### 6) Security & Privacy
- Proof‑of‑possession (PoP) cho B trước khi re‑encrypt hoặc cấp rk (nonce challenge, ký bằng sk_B).
- Tag/Epoch binding: rust-umbral không bind tag ở crypto; cưỡng chế bằng Central token/rk-scope (bắt buộc chứa {A,B,policyTag[,fileId]}, TTL ngắn, chữ ký Central). Store chỉ transform khi token hợp lệ.
- Authorization: Central kiểm tra follow + entitlement cho epoch (followYear(B→A) ≤ epoch).
- Audit: Central/Store log (A,B,fileId,tag,timestamp,mode) mỗi lần transform.
- Ưu tiên proxy flow để tránh phát rk xuống client; nếu client flow dùng, rk phải ngắn hạn và scope chặt (tag[,fileId]).

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

### 10) Node‑local PRE integration (không dùng PRE service chung)

- Yêu cầu: App, Central API, Store là các node độc lập, phân tán. Mỗi node tích hợp PRE cục bộ của chính mình. Không triển khai một PRE proxy/service chung.

- Lựa chọn thư viện
  - Ưu tiên Rust (Umbral/CPRE implementation) build thành:
    - Mobile/Desktop: `.so` (Android, Linux), `.dylib` (macOS/iOS), `.dll` (Windows)
    - Web: WASM + glue JS
  - Cấp API C/ABI tối giản để các host (Dart/Flutter, .NET) gọi qua FFI/PInvoke/WASM.

- Chức năng bắt buộc của PRE lib (đồng nhất cho mọi node)
  - `encapsulate_with_tag(pk_author, tag, K) -> PRE bundle [capsule,ciphertext(K),pkA]`
  - `generate_rekey(sk_author, pk_recipient, tag[, scope]) -> rk_A_to_B_tag`
  - `reencrypt(bundle, rk_A_to_B_tag) -> bundle_for_B (append verifying_pk + cfrag)`
  - `decapsulate(sk_recipient, bundle_for_B) -> K`
  - Tất cả buffer cần zeroize sau khi dùng; API trả mã lỗi chuẩn.

- Tích hợp theo từng node
  - App (Flutter)
    - Native (iOS/Android/macOS/Windows/Linux): dùng `dart:ffi` gọi lib Rust.
    - Web: nạp WASM; wrap bằng `package:js`/`dart:js_interop`.
    - Dùng trong `PreService`:
      - `createTaggedCapsule(K, pkA, tag)` gọi `encapsulate_with_tag`.
      - `generateRekey(skA, pkB, tag)` gọi `generate_rekey` (khi có follower mới/đầu năm).
      - Fallback client: `transformCapsule(capsule, rk)` gọi `reencrypt` nếu chọn chế độ client.
    - Bảo mật:
      - sk_A/sk_B sinh từ mnemonic; giữ in‑memory tối thiểu, không log; zeroize sau dùng.
      - rk chỉ sinh khi cần, upload Central; không lưu rk lâu dài.
    - Nhiệm vụ định kỳ:
      - Đồng bộ followers mới → sinh rk_{A→B}^{tag} và upload Central.
      - Rollover đầu năm → sinh rk cho tag mới.

  - Store node (C#)
    - Tích hợp lib Rust qua P/Invoke (`DllImport`) hoặc wrapper .NET (nếu có).
    - Endpoint `/api/pre/reencrypt`:
      - Xác thực yêu cầu (headers); verify token Central cho (A,B,tag[,fileId]).
      - Gọi `reencrypt(bundle, rk|token)` từ lib/tầng policy → trả `EncryptedKeyForB` (PRE bundle đã transform).
      - Không giữ sk_A/sk_B; rk chỉ dùng tạm thời; zeroize buffer; rate‑limit; audit.
    - Không có PRE service chung: mỗi Store chạy lib PRE riêng, độc lập update/triển khai.

  - Central API (C#)
    - Không thực hiện re‑encrypt; chỉ xác thực PoP, kiểm tra quyền/epoch, lưu rk (hoặc cấp token ngắn hạn để Store lấy rk).
    - Tích hợp lib PRE là không bắt buộc (Central không transform); chỉ quản lý rk/token.
    - Chính sách:
      - PoP (B ký nonce) trước khi cấp rk/token.
      - Entitlement: chỉ cấp rk cho `followers:<YYYY>` với `YYYY >= followYear(B→A)`.
      - Ưu tiên token cho Store (proxy mode), chỉ trả rk cho client khi fallback.

- Kịch bản hoạt động end‑to‑end (không PRE service chung)
  1) App A đăng bài followers (Option 2): tạo K; `encapsulate_with_tag(pkA, tag, K)` → `encryptedKey = capsule`.
  2) A theo dõi followers và epoch:
     - `generate_rekey(skA, pkB, tag)` cho từng B đủ quyền → `upload-rekey` lên Central.
  3) App B đọc bài:
     - PoP với Central → nhận token (proxy mode) hoặc rk (client mode fallback).
     - Proxy mode: B gọi Store `/api/pre/reencrypt` (Store kiểm tra token, tải rk nếu cần) → Store gọi lib cục bộ `reencrypt(capsule, rk)` → trả `C_B(K)`.
     - Client mode: B nhận rk, gọi lib cục bộ `reencrypt(capsule, rk)` → `C_B(K)`.
     - B gọi `decapsulate(skB, C_B(K))` → lấy K, giải nội dung.

- Build & phát hành (đa nền tảng)
  - Rust lib: build matrix (Android/arm64‑v8a, iOS/arm64, macOS/x86_64+arm64, Windows/x64, Linux/x64) + WASM.
  - App: gói artifacts theo nền tảng; dynamic loading qua `dart:ffi`/WASM loader.
  - Store/Central: include native lib trong publish; cấu hình path nạp DLL/DYLIB/SO.
  - Phiên bản hóa: cùng version PRE lib giữa các node để tránh sai khác định dạng capsule/rk.

-- Định dạng dữ liệu
  - `encryptedKey` (PRE bundle) = base64 của blob PRE dạng length-prefixed:
    - [u32 cap_len][cap]
    - [u32 ct_len][ciphertext_of_K]
    - [u32 pkA_len][pkA]
    - Sau khi re-encrypt append:
      - [u32 vpk_len][verifying_pk]
      - [u32 cfrag_len][cfrag]
  - `rk` = base64; scope theo `tag` (và tùy chọn `fileId`) do Central quản lý.
  - `token` proxy (JSON, ký bởi Central): { followingPubKey, followerPublicKey, tag[, fileId], exp, signature } cho phép Store transform.

- Kiểm thử & an toàn
  - Vectors TEST: round‑trip encapsulate → reencrypt → decapsulate.
  - Negative: tag sai, epoch sai, PoP sai, rk sai → từ chối.
  - Bench: latency transform; memory zeroization; fuzz inputs (capsule/rk) với lib Rust.


### 11) Implementation status & notes (CURRENT)

- App (Flutter)
  - UI: Toggle Followers Option 1/Option 2 đã thêm trong create post.
  - Tạo bài (Option 2):
    - Sinh K bằng RNG an toàn; `PostService.createPost` gọi FFI `encapsulate_with_tag` qua `PreFfi` để tạo PRE bundle và inject vào `FileCryptoService.encryptAndSplitFile` qua `injectedSymmetricKey` và `preEncryptedKeyBundle`.
    - Metadata ghi `encryptedKey` = PRE bundle (base64), `capsuleFor="tag"`, `policyTag`, `policyScheme="CPRE"`.
  - Đọc bài (Followers feed):
    - Nếu `capsuleFor!='tag'` → giải theo Option 1.
    - Nếu `capsuleFor=='tag'` → gọi Store `/api/file/pre/reencrypt` (proxy mode) để nhận bundle đã transform; `PreService.decapsulateAndRecoverKey` gọi FFI `decapsulate_for_recipient` để lấy K, giải nội dung.
    - Fallback client mode đã có: `PreService.clientTransform` (PoP → Central `/policy/fetch-rekey?mode=client` → FFI `reencrypt`).
  - Rekey (tác giả):
    - `PreService.publishTag`, `uploadRekey`, `generateAndUploadRekeys` (sinh rk qua FFI `generate_rekey` và upload Central) đã triển khai; hiện tự động gọi sau khi đăng bài Followers+PRE. Có thể gọi khi khởi động/đồng bộ theo epoch.
  - FFI/Loader:
    - `lib/services/pre_ffi.dart` wraps C‑ABI: `umbral_encapsulate_with_tag`, `umbral_generate_rekey`, `umbral_reencrypt`, `umbral_decapsulate_for_recipient`, `umbral_free`.
    - macOS: loader thử `libumbral_pre.dylib` và fallback `@executable_path/../Frameworks/libumbral_pre.dylib`.

- Store
  - `/api/file/pre/reencrypt` xác thực token Central và gọi core Umbral PRE để transform; trả `encryptedKeyForB`.
  - `followers-feed` trả đủ Option 1/2 + PRE fields.

- Central API
  - `PolicyController`: `nonce`, `publish-tag`, `upload-rekey`, `fetch-rekey`, `validate-token` hoàn chỉnh; token scope theo tag (và tùy chọn fileId), TTL ngắn.

- Định dạng bundle
  - Theo phần "Định dạng dữ liệu" (length‑prefixed). App có helper để trích xuất `pkA` từ bundle cho client‑transform.


### 12) Build & packaging (Flutter CURRENT)

- Script: `deltanium-core/scripts/build_umbral_pre_flutter.sh`
  - Android: `--android --app-dir <path-to-app>` → copy `.so` vào `android/app/src/main/jniLibs/{arm64-v8a,armeabi-v7a,x86_64}`.
  - macOS: `--macos --app-dir <path-to-app>` → copy `.dylib` vào `macos/Frameworks`. Khuyến nghị thêm Copy Files phase để bundle trong app.
  - iOS: `--ios` (cargo-lipo nếu có) → tạo static lib universal; cần add vào Runner và link thủ công.

- Loader macOS: fallback về `@executable_path/../Frameworks/libumbral_pre.dylib` đã implement để tiện dev.

- Việc còn lại (không chặn dev trên macOS):
  - Thêm Copy Files phase cho macOS để bundle `.dylib` trong release.
  - Hoàn tất tích hợp iOS (add static lib/xcframework vào Runner) và Android (đảm bảo jniLibs có đủ ABI cần thiết).
  - (Tùy chọn) UI toggle cho proxy/client transform.
  - (Tùy chọn) Trigger tự động generate/upload rk lúc khởi động app hoặc khi có follower mới / đổi epoch.





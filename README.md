# Deltanium API

A centralized API service for the Deltanium ecosystem, managing user registrations, store/blocker coordination, PRE rekeys, and N-of-M file placement index.

## Production

| | URL |
|--|-----|
| **Central API** | [https://api.deltanium.com](https://api.deltanium.com) |
| Client base path | [https://api.deltanium.com/api](https://api.deltanium.com/api) |
| App | [https://app.deltanium.com](https://app.deltanium.com) |
| Landing | [https://deltanium.com](https://deltanium.com) |

Local default: `http://127.0.0.1:5002`.

## Features

- User registration with cryptographic key pairs and signature verification
- Store / Block Creator node registration (atomic one-shot vouchers)
- Node discovery for file operations
- Secure key generation using NBitcoin
- PRE rekey management (delegated generation, Option 3)
- Signed request auth (`SignatureAuthHelper`) on sensitive write paths
- N-of-M stored-file index (`register` + `register-replicas`)
- Persistence: **MySQL** (default) with JSON fallback; secrets not committed

## User Registration Flow

1. **Key Generation (Optional)**
   - Clients may generate keys client-side and register directly.
   - The API also provides `GET /api/user/generateKeys` which returns a 12-word mnemonic and public key if clients prefer server-assisted key generation (for development/demo).

2. **Register User**
   - `POST /api/user/register`
   - Request body:
```json
{
       "publicKey": "...",
       "email": "...",
       "fullName": "...",
       "dateOfBirth": "YYYY-MM-DD",
       "bio": "...",
       "avatarUrl": "...",
       "timestamp": 1700000000,
       "signature": "..." // Signature of "email:fullName:yyyy-MM-dd:timestamp" using the private key
}
```
   - The backend verifies:
     - Signature correctness against `email:fullName:yyyy-MM-dd:timestamp`
     - `timestamp` is within a 5-minute window to prevent replay
     - Email format and uniqueness; public key not previously registered
   - On success, the user is stored along with their signature and registration timestamp.

3. **Verify Registration**
   - `GET /api/user/verify/{publicKey}`

4. **Get User Profile**
   - `GET /api/user/profile/{publicKey}`

## Following & Social Graph

- **Get Following**
  - `GET /api/user/following`
  - Headers: `X-User-PubKey`
  - Returns a list of users the caller is following.

- **Get Followers**
  - `GET /api/user/followers`
  - Headers: `X-User-PubKey`
  - Returns a list of users who follow the caller.

- **Follow a User**
  - `POST /api/user/follow`
  - Headers: `X-User-PubKey`
  - Body includes: `targetPublicKey`, `timestamp`, `signedData`, `signature`
  - The API verifies the signature on `signedData` and that the target user exists, then records the follow.

- **Unfollow a User**
  - `POST /api/user/unfollow`
  - Headers: `X-User-PubKey`
  - Body includes: `targetPublicKey`, `timestamp`, `signedData`, `signature`
  - The API removes the follow relationship if present.

## Store Node Management

- Register, list, and manage storage nodes via `/api/storenode/*` endpoints.
  - `POST /api/storenode/register` (voucher-based): `{ endpoint, voucher, publicKey }`
    - **Atomic** voucher consume (`TryMarkVoucherUsedAsync`): only one concurrent register wins.
    - Prevents duplicate registration by public key.
  - `GET /api/storenode/list`: lists active/registered storage nodes for clients to discover where to upload content.

## Stored File Indexing (N-of-M)

Central API indexes placements; Store nodes hold ciphertext. Same `fileId` may appear on multiple nodes.

- `POST /api/storedfile/register` — single placement (signed by owner)
- `POST /api/storedfile/register-replicas` — batch `{ fileId, ownerPublicKey, storeNodeIds[], fileHash, fileSize }` (signed by owner)
- `GET /api/storedfile/nodes/{publicKey}` — nodes storing a user’s files
- `GET /api/storedfile/details/{publicKey}` — nodes with file details

App sets replica count via `REPLICATION_FACTOR` (default 2) and uploads full replicas to N stores, then calls `register-replicas`.

## Transactions

- `POST /api/tx/submit` — signed; authenticated caller must match `From`
- `GET /api/tx/pull` — Blocker-only; requires `X-Internal-Token` (`InternalApi:Token` / `INTERNAL_API_TOKEN`)

## Setup and Installation

1. Prerequisites:
   - .NET 7.0 SDK or later
   - Git

2. Clone the repository:
   ```bash
   git clone <repository-url>
   cd deltanium-api
   ```

3. Install dependencies:
   ```bash
   dotnet restore
   ```

4. Run the application:
   ```bash
   dotnet run
   ```

The API will start on `http://localhost:5002` by default.

## Data Storage

**Preferred:** MySQL (`Storage:Provider=MySql`). Set connection via User Secrets:

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=deltanium_api;..." --id deltanium-api-local-secrets
```

**JSON fallback** (local/dev only; gitignored — do not commit):

- `users.json`, `user_follows.json`, `nodes.json`, `vouchers.json`, `stored_files.json`, `user_rekeys.json`
- Examples only: `vouchers.json.example`, `central_keys.json.example`

**Central signing keys:** env `CENTRAL_PUBLIC_KEY` + `CENTRAL_PRIVATE_KEY`, or `CENTRAL_KEYS_PATH` / `~/.deltanium/central_keys.json` (never commit).

## Security

- NBitcoin for crypto; clients should generate keys locally in production
- Request auth: `METHOD + path+query + timestamp + sha256hex(body)` with headers `X-User-PubKey`, `X-Timestamp`, `X-Signature` (5-minute window)
- Applied on policy uploads, stored-file register/replicas, tx submit, etc.
- Internal token for Store/Blocker → API (`validate-token`, `tx/pull`)
- CORS: fail-closed outside Development unless `Cors:AllowedOrigins` is set
- Vouchers for node registration are one-shot and consumed atomically

## PRE Rekey Management (Option 3 - Delegated Generation)

Authors generate rekeys locally and upload them to the Central API. The API stores and serves rekeys to authorized followers.

### Endpoints

- `GET /api/policy/nonce?userPubKey={followerPubKey}`
  - Issues a one-time nonce for proof-of-possession (PoP) by the follower.

- `POST /api/policy/upload-rekeys-batch`
  - Author uploads a batch of rekeys after login sync.
  - Headers:
    - `X-User-PubKey`: author public key
    - `X-Timestamp`
    - `X-Signature`: signature of `method + "/api/policy/upload-rekeys-batch" + timestamp + sha256Hex(body)`
  - Body (JSON):
    ```json
    {
      "rekeys": [
        {
          "followingPubKey": "<author pk>",
          "followerPubKey": "<follower pk>",
          "tag": "followers:2025",
          "rkBase64": "<base64 rekey>",
          "createdAt": "2025-10-22T10:00:00Z"
        }
      ]
    }
    ```

- `POST /api/policy/fetch-rekey`
  - Follower fetches a stored rekey.
  - Two-stage auth:
    1) Get `nonce` via `GET /api/policy/nonce?userPubKey={follower}` and sign it with follower’s key (PoP)
    2) POST body includes `proof: { nonce, signature }`
  - Headers:
    - `X-User-PubKey`: follower public key
    - `X-Timestamp`
    - `X-Signature`: signature of `method + "/api/policy/fetch-rekey" + timestamp + sha256Hex(body)`
  - Body (JSON):
    ```json
    {
      "followerPubKey": "<B>",
      "followingPubKey": "<A>",
      "tag": "followers:2025",
      "mode": "client",
      "proof": { "nonce": "...", "signature": "<sig(nonce)>" }
    }
    ```
  - Responses:
    - `200 OK`: `{ "rekey": "<base64>" }`
    - `404 Not Found`: rekey not yet uploaded by author

### Author Login Rekey Sync Flow

1) App calls `GET /api/user/get-new-followers` and `GET /api/user/get-followers-without-rekey` (headers: `X-User-PubKey`), to determine targets.
2) App locally generates `rk(A→B)` using Umbral PRE FFI and uploads via `upload-rekeys-batch`.
3) Followers subsequently fetch rekeys via `fetch-rekey` when decrypting PRE content.

## Development

- The API uses Swagger UI for testing and documentation
- Access Swagger UI at `http://localhost:5002/swagger` when running in Development mode

## License

[Your License Here] 
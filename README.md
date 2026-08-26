# Deltanium API

A centralized API service for the Deltanium ecosystem, managing user registrations and store node coordination.

## Features

- User registration with cryptographic key pairs and signature verification
- Store node registration and management
- Node discovery for file operations
- Secure key generation using NBitcoin
- PRE rekey management (delegated generation, Option 3)
- File-based storage (upgradeable to database)

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
    - Validates voucher (unused), prevents duplicate registration by public key, stores node, marks voucher as used.
  - `GET /api/storenode/list`: lists active/registered storage nodes for clients to discover where to upload content.

## Stored File Indexing

- Endpoints under `/api/storedfile/*` allow the central API to index which files are stored on which nodes (optional but useful for discovery):
  - `POST /api/storedfile/register`: register a file reference `{ ownerPublicKey, storeNodeId, ... }`
  - `GET /api/storedfile/nodes/{publicKey}`: list nodes storing files for a user
  - `GET /api/storedfile/details/{publicKey}`: list nodes with file details for a user

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

These files are generated at runtime and must not be committed:

  - `users.json`: User registration data
  - `user_follows.json`: Follow relationships (follower → following)
  - `nodes.json`: Store node information
  - `vouchers.json`: Store node registration vouchers (unused/used). Copy from `vouchers.json.example` for local development.
  - `stored_files.json`: Indexed references of files and their storage nodes
  - `user_rekeys.json`: Cached PRE rekeys uploaded by authors (delegated generation)
  - `central_keys.json`: Central API signing key pair. Created automatically on first run. See `central_keys.json.example`.

## Security

- Uses NBitcoin for cryptographic operations
- Can generate secure 12-word mnemonic phrases for key recovery (via API), though clients may generate keys locally
- Public keys are used for identity verification
- Registration and follow/unfollow requests include signatures and timestamps (5-minute validity window) to prevent replay
- CORS is enabled for all origins (customize as needed)

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
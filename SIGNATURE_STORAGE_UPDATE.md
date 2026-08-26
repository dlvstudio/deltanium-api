# Signature Storage with Timestamp Update

## Overview

This update stores the user's registration signature together with the timestamp so they can be reused for later blockchain operations. The timestamp is included in the signed payload to improve security and prevent replay attacks.

## Changes

### 1. Model updates

#### User model (`Models/User.cs`)
- Added `Signature` to store the registration signature
- Added `RegistrationTimestamp` to store the timestamp used in that signature
```csharp
public string Signature { get; set; } // Registration signature for blockchain operations
public long? RegistrationTimestamp { get; set; } // Unix timestamp used in registration signature
```

#### SignedRegisterRequest model (`Models/Requests/SignedRegisterRequest.cs`)
- Added `Timestamp` sent by the client
```csharp
public long Timestamp { get; set; } // Unix timestamp for signature validation
```

### 2. API controller updates (`Controllers/UserController.cs`)

#### Register endpoint
- Validate timestamp (not too old or too far in the future)
- Verify signature over `email:fullName:yyyy-MM-dd:timestamp`
- Persist both signature and timestamp
```csharp
// Validate timestamp
var requestTime = DateTimeOffset.FromUnixTimeSeconds(request.Timestamp);
var now = DateTimeOffset.UtcNow;
var timeDiff = Math.Abs((now - requestTime).TotalMinutes);
if (timeDiff > 5) // 5 minutes window
{
    return BadRequest("Timestamp too old or too far in future");
}

// Verify signature with timestamp
var dataToVerify = $"{request.Email}:{request.FullName}:{request.DateOfBirth?.ToString("yyyy-MM-dd")}:{request.Timestamp}";
```

#### UpdateProfile endpoint
- Include timestamp in signature verification
- Store a new timestamp when the profile is updated

#### GetUsersWithSignatures endpoint
- Include `RegistrationTimestamp` in the response
- Endpoint: `GET /api/user/signatures`

### 3. Frontend updates (deltanium-app)

#### SignedRegisterRequest model (`lib/models/signed_register_request.dart`)
- Added `timestamp`
- Updated `getSignableData()` to include timestamp
```dart
String getSignableData() {
  return '$email:$fullName:${dateOfBirth?.toString().split(' ')[0]}:$timestamp';
}
```

#### Registration logic
- Generate timestamp: `DateTime.now().millisecondsSinceEpoch ~/ 1000`
- Include timestamp in the signature
- Send timestamp in the request body

### 4. Database update (`users.json`)
- Added `RegistrationTimestamp: null` for existing users
- Newly registered users store both signature and timestamp

## Usage

### Register a new user (signature + timestamp)
```dart
// Client generates timestamp
final timestamp = (DateTime.now().millisecondsSinceEpoch ~/ 1000);

// Create signable data with timestamp
final signableData = '$email:$fullName:$dateOfBirth:$timestamp';

// Sign data including timestamp
final signature = await CryptoService.sign(signableData, keyPair);

// Send request with signature and timestamp
final request = {
  'publicKey': publicKey,
  'email': email,
  'fullName': fullName,
  'dateOfBirth': dateOfBirth,
  'signature': signature,
  'timestamp': timestamp,
};
```

### List users that have a signature
```http
GET /api/user/signatures
```
Response:
```json
[
  {
    "id": "user-id",
    "publicKey": "public-key",
    "email": "user@example.com",
    "fullName": "User Name",
    "dateOfBirth": "2007-05-15T00:00:00",
    "signature": "base64-signature",
    "registrationTimestamp": 1641234567,
    "createdAt": "2025-01-15T10:30:00Z"
  }
]
```

### Blockchain use
- The signature was verified with a timestamp at registration
- Signature + timestamp can be used to publish user info onto the blockchain
- Signature is over `email:fullName:yyyy-MM-dd:timestamp`
- Timestamp provides uniqueness and replay protection

## Security

### Timestamp validation
- The API rejects timestamps older than 5 minutes or too far in the future
- Replay attacks are harder because the signature includes a unique timestamp
- Requests must be created close to the time they are sent

### Signature security
- The signature covers the important fields: email, fullName, dateOfBirth, timestamp
- Each registration has a unique signature because of the timestamp
- Old signatures cannot be replayed because of timestamp validation

## Backward compatibility
- Existing users may have `Signature: null` and `RegistrationTimestamp: null` and still work
- New users automatically store both signature and timestamp
- Existing features are unaffected

## Future enhancements
- Endpoint for a user to re-create a signature with a new timestamp
- Batch endpoint to push many users to the blockchain at once
- Blockchain service integration to auto-push user data with signature + timestamp
- Signature expiration policy based on timestamp

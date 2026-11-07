# Signature Storage with Timestamp Update

## Tổng quan
Cập nhật này thêm khả năng lưu trữ chữ ký (signature) cùng với timestamp của user trong quá trình đăng ký để có thể sử dụng cho các thao tác blockchain sau này. Timestamp được bao gồm trong signature để đảm bảo tính bảo mật và chống replay attacks.

## Các thay đổi đã thực hiện

### 1. Model Updates

#### User Model (Models/User.cs)
- Thêm field `Signature` để lưu trữ chữ ký đăng ký
- Thêm field `RegistrationTimestamp` để lưu trữ timestamp được dùng trong signature
```csharp
public string Signature { get; set; } // Registration signature for blockchain operations
public long? RegistrationTimestamp { get; set; } // Unix timestamp used in registration signature
```

#### SignedRegisterRequest Model (Models/Requests/SignedRegisterRequest.cs)
- Thêm field `Timestamp` để gửi timestamp từ client
```csharp
public long Timestamp { get; set; } // Unix timestamp for signature validation
```

### 2. API Controller Updates (Controllers/UserController.cs)

#### Register Endpoint
- Validate timestamp (không quá cũ hoặc quá xa trong tương lai)
- Verify signature bao gồm timestamp: `email:fullName:yyyy-MM-dd:timestamp`
- Lưu cả signature và timestamp vào database
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

#### UpdateProfile Endpoint
- Cập nhật để cũng bao gồm timestamp trong signature verification
- Lưu timestamp mới khi update profile

#### GetUsersWithSignatures Endpoint
- Bao gồm `RegistrationTimestamp` trong response
- Endpoint: `GET /api/user/signatures`

### 3. Frontend Updates (deltanium-app)

#### SignedRegisterRequest Model (lib/models/signed_register_request.dart)
- Thêm field `timestamp`
- Cập nhật `getSignableData()` để bao gồm timestamp
```dart
String getSignableData() {
  return '$email:$fullName:${dateOfBirth?.toString().split(' ')[0]}:$timestamp';
}
```

#### Registration Logic
- Generate timestamp: `DateTime.now().millisecondsSinceEpoch ~/ 1000`
- Bao gồm timestamp trong signature
- Gửi timestamp trong request body

### 4. Database Update (users.json)
- Thêm field `RegistrationTimestamp: null` cho tất cả user hiện có
- User mới đăng ký sẽ có cả signature và timestamp được lưu

## Cách sử dụng

### Đăng ký user mới (có signature + timestamp)
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

### Lấy danh sách user có signature
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

### Sử dụng cho blockchain
- Signature đã được verify với timestamp khi đăng ký
- Có thể sử dụng signature + timestamp để đẩy thông tin user lên blockchain
- Signature được tạo từ data: `email:fullName:yyyy-MM-dd:timestamp`
- Timestamp đảm bảo tính duy nhất và chống replay attacks

## Bảo mật

### Timestamp Validation
- API validate timestamp không quá cũ (>5 phút) hoặc quá xa trong tương lai
- Chống replay attacks vì signature bao gồm timestamp duy nhất
- Đảm bảo request được tạo gần thời điểm gửi

### Signature Security
- Signature bao gồm tất cả thông tin quan trọng: email, fullName, dateOfBirth, timestamp
- Mỗi registration có signature duy nhất do timestamp
- Không thể replay signature cũ do timestamp validation

## Backward Compatibility
- User cũ có `Signature: null` và `RegistrationTimestamp: null` - vẫn hoạt động bình thường
- User mới sẽ có cả signature và timestamp được lưu tự động
- Không ảnh hưởng đến các chức năng hiện có

## Future Enhancements
- Có thể thêm endpoint để user tạo lại signature với timestamp mới nếu cần
- Thêm batch endpoint để đẩy nhiều user lên blockchain cùng lúc
- Tích hợp với blockchain service để auto-push user data với signature + timestamp
- Implement signature expiration policy dựa trên timestamp 
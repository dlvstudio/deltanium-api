# Kế Hoạch Unit Test cho Deltanium API

## 1. Tổng Quan

### Mục Tiêu
- Đảm bảo chất lượng code và tính ổn định
- Phát hiện bugs sớm trong quá trình development
- Tài liệu hóa behavior của các components
- Hỗ trợ refactoring an toàn

### Coverage Goals
- **Controllers**: 80%+ coverage
- **Services**: 90%+ coverage
- **Critical paths**: 100% coverage (user registration, node registration)

## 2. Cấu Trúc Test Project

```
deltanium-api.Tests/
├── Controllers/          # (planned / partial)
├── Services/
│   ├── KeyServiceTests.cs
│   ├── StorageServiceTests.cs
│   ├── MySqlStorageServiceTests.cs      # InMemory EF
│   └── SignatureAuthHelperTests.cs      # signed request auth
├── Integration/
│   └── RealMysqlStorageIntegrationTests.cs  # remote MySQL (User Secrets)
├── Helpers/
│   ├── TestDataBuilder.cs
│   ├── InMemoryDb.cs
│   ├── RealMysql.cs
│   └── TestFileHelper.cs
└── deltanium-api.Tests.csproj
```

### Real MySQL integration (đã có)

Chạy khi có `deltanium-api-local-secrets` hoặc `DELTANIUM_MYSQL_CS`:

```bash
dotnet test --filter "FullyQualifiedName~RealMysql"
```

Cases quan trọng:

| Test | Mục đích |
|------|----------|
| `RealDb_RegisterFollowRekey_AndPullTransactions` | user / follow / rekey / voucher / file / tx pull |
| `RealDb_TryMarkVoucherUsed_IsAtomic` | voucher one-shot trên SQL thật |
| `RealDb_SaveStoredFilesBatch_RegistersNofMReplicas` | N-of-M index cùng `fileId` |
| `RealDb_SignatureAuth_AgainstRegisteredUser` | ký request thật + user đã đăng ký |
| `RealDb_HttpRegister_WithValidSignature_RoundTrip` | crypto đăng ký |

## 3. Test Cases Chi Tiết

### 3.1. KeyService Tests

#### Test Cases:
1. **GenerateKeyPair**
   - ✅ Generate returns valid mnemonic (12 words)
   - ✅ Generate returns valid public key (secp256k1)
   - ✅ Generate returns different keys on each call
   - ✅ Mnemonic can derive same key pair

2. **ValidatePublicKey**
   - ✅ Valid compressed public key (66 chars, starts with 02/03)
   - ✅ Valid uncompressed public key (130 chars, starts with 04)
   - ✅ Invalid: null/empty string
   - ✅ Invalid: wrong length
   - ✅ Invalid: invalid hex characters
   - ✅ Invalid: wrong prefix

3. **VerifySignature**
   - ✅ Valid signature verification
   - ✅ Invalid signature (wrong key)
   - ✅ Invalid signature (wrong message)
   - ✅ Invalid signature (tampered)
   - ✅ Invalid signature format (not base64)
   - ✅ Edge cases: empty message, null signature

### 3.2. StorageService Tests

#### Test Cases:
1. **User Management**
   - ✅ SaveUserAsync - saves new user
   - ✅ SaveUserAsync - throws on duplicate public key
   - ✅ GetUsersAsync - returns all users
   - ✅ GetUsersAsync - returns empty list when no users
   - ✅ IsPublicKeyRegisteredAsync - returns true for registered key
   - ✅ IsPublicKeyRegisteredAsync - returns false for unregistered key
   - ✅ UpdateUserAsync - updates existing user
   - ✅ UpdateUserAsync - throws on non-existent user

2. **Node Management**
   - ✅ SaveNodeAsync - saves new store node
   - ✅ GetNodesAsync - returns all nodes
   - ✅ GetActiveNodesAsync - returns only active nodes
   - ✅ SaveBlockCreatorNodeAsync - saves new block creator node
   - ✅ GetBlockCreatorNodesAsync - returns all block creator nodes
   - ✅ GetActiveBlockCreatorNodesAsync - returns only active block creator nodes

3. **Voucher Management**
   - ✅ IsVoucherValidAsync - returns true for valid unused voucher
   - ✅ IsVoucherValidAsync - returns false for used voucher
   - ✅ IsVoucherValidAsync - returns false for non-existent voucher
   - ✅ MarkVoucherUsedAsync - marks voucher as used
   - ✅ MarkVoucherUsedAsync - handles non-existent voucher gracefully

4. **Follow Management**
   - ✅ FollowUserAsync - creates follow relationship
   - ✅ FollowUserAsync - returns false for self-follow
   - ✅ FollowUserAsync - returns false for duplicate follow
   - ✅ UnfollowUserAsync - removes follow relationship
   - ✅ IsFollowingAsync - returns correct follow status
   - ✅ GetFollowingPublicKeysAsync - returns correct list
   - ✅ GetFollowersPublicKeysAsync - returns correct list

5. **File Management**
   - ✅ SaveStoredFileAsync - saves file record
   - ✅ GetStoredFilesAsync - returns all files
   - ✅ GetNodesStoringUserFilesAsync - returns correct nodes
   - ✅ GetUserFilesOnNodeAsync - returns correct files

6. **Rekey Management**
   - ✅ SaveRekeyAsync - saves rekey
   - ✅ GetUserRekeysAsync - returns all rekeys
   - ✅ GetActiveRekeysAsync - returns only active rekeys
   - ✅ DeactivateRekeysAsync - deactivates rekeys

### 3.3. UserController Tests

#### Test Cases:
1. **GenerateKeys**
   - ✅ Returns valid mnemonic and public key
   - ✅ Returns different keys on each call

2. **Register**
   - ✅ Success: Valid registration with all fields
   - ✅ Success: Valid registration with minimal fields
   - ✅ Success: Returns user with central signature
   - ✅ BadRequest: Invalid email format
   - ✅ BadRequest: Missing email
   - ✅ BadRequest: Invalid timestamp (too old)
   - ✅ BadRequest: Invalid timestamp (too far future)
   - ✅ BadRequest: Duplicate email
   - ✅ BadRequest: Duplicate public key
   - ✅ BadRequest: Invalid signature
   - ✅ BadRequest: Missing signature
   - ✅ Success: Broadcasts to Block Creator nodes
   - ✅ Success: Central API signs the transaction

3. **VerifyRegistration**
   - ✅ Returns true for registered public key
   - ✅ Returns false for unregistered public key
   - ✅ BadRequest: Invalid public key format

4. **GetProfile**
   - ✅ Returns user profile for valid public key
   - ✅ NotFound: Non-existent public key
   - ✅ BadRequest: Invalid public key format

5. **UpdateProfile**
   - ✅ Success: Updates user profile
   - ✅ BadRequest: Invalid signature
   - ✅ BadRequest: Email already taken by another user
   - ✅ NotFound: User not found

6. **SearchUsers**
   - ✅ Returns matching users by name
   - ✅ Returns matching users by public key
   - ✅ Excludes current user from results
   - ✅ Unauthorized: Missing authentication headers
   - ✅ Unauthorized: Invalid signature

7. **Follow**
   - ✅ Success: Follows user successfully
   - ✅ BadRequest: Invalid signature
   - ✅ BadRequest: Target user not found
   - ✅ BadRequest: Cannot follow yourself
   - ✅ BadRequest: Already following
   - ✅ BadRequest: Timestamp mismatch
   - ✅ Unauthorized: Missing/invalid authentication

8. **Unfollow**
   - ✅ Success: Unfollows user
   - ✅ BadRequest: Not following user
   - ✅ Unauthorized: Missing/invalid authentication

9. **GetFollowing/GetFollowers**
   - ✅ Returns correct list of following/followers
   - ✅ Returns empty list when none
   - ✅ Unauthorized: Missing/invalid authentication

10. **GetNewFollowers**
    - ✅ Returns new followers since timestamp
    - ✅ Returns empty list when none
    - ✅ Unauthorized: Missing/invalid authentication

11. **GetFollowersWithoutRekey**
    - ✅ Returns followers without rekey for tag
    - ✅ BadRequest: Missing tag
    - ✅ Unauthorized: Missing/invalid authentication

### 3.4. StoreNodeController Tests

#### Test Cases:
1. **Register**
   - ✅ Success: Valid registration with voucher
   - ✅ BadRequest: Missing endpoint
   - ✅ BadRequest: Missing voucher
   - ✅ BadRequest: Missing public key
   - ✅ BadRequest: Invalid voucher
   - ✅ BadRequest: Already used voucher
   - ✅ Conflict: Node already registered (same public key)
   - ✅ Success: Marks voucher as used

2. **ListNodes**
   - ✅ Returns all active nodes
   - ✅ Returns empty list when no nodes

### 3.5. BlockCreatorNodeController Tests

#### Test Cases:
1. **Register**
   - ✅ Success: Valid registration with voucher
   - ✅ BadRequest: Missing endpoint
   - ✅ BadRequest: Missing voucher
   - ✅ BadRequest: Missing public key
   - ✅ BadRequest: Invalid public key format
   - ✅ BadRequest: Invalid voucher
   - ✅ BadRequest: Already used voucher
   - ✅ Conflict: Node already registered (same public key)
   - ✅ Success: Marks voucher as used

2. **ListNodes**
   - ✅ Returns all active block creator nodes
   - ✅ Returns empty list when no nodes

### 3.5b. SignatureAuthHelper & N-of-M (đã thêm)

1. **SignatureAuthHelper**
   - ✅ Valid signed POST
   - ✅ Missing headers / expired timestamp / unregistered user
   - ✅ `requireRegisteredUser: false` cho tx submit path
2. **MySqlStorageService**
   - ✅ `TryMarkVoucherUsedAsync` first=true, second=false
   - ✅ `SaveStoredFilesBatchAsync` two replicas same `fileId`

### 3.6. StoredFileController Tests

#### Test Cases:
1. **GetNodesStoringUserFiles**
   - ✅ Returns nodes storing user files
   - ✅ BadRequest: Invalid public key format
   - ✅ Returns empty list when no files

2. **GetDetailedNodesWithFiles**
   - ✅ Returns nodes with files
   - ✅ BadRequest: Invalid public key format

3. **RegisterStoredFile**
   - ✅ Success: Registers file
   - ✅ BadRequest: Invalid owner public key
   - ✅ BadRequest: Invalid store node ID

### 3.7. PolicyController Tests

#### Test Cases:
1. **GenerateRekey** (if applicable)
   - ✅ Success: Generates rekey
   - ✅ BadRequest: Invalid parameters

2. **GetRekeys** (if applicable)
   - ✅ Returns rekeys for user
   - ✅ Returns empty list when none

## 4. Test Strategy

### 4.1. Mocking Strategy

1. **File System**
   - Mock file operations using in-memory file system
   - Use temporary directories for integration tests
   - Clean up after each test

2. **HTTP Clients**
   - Mock HttpClient for Block Creator node communication
   - Use Moq or NSubstitute for mocking

3. **Dependencies**
   - Mock StorageService for Controller tests
   - Mock KeyService for Controller tests
   - Use real implementations for Service tests

### 4.2. Test Data Management

1. **Test Data Builder**
   - Create helper class to build test data
   - Methods: `CreateUser()`, `CreateNode()`, `CreateVoucher()`, etc.
   - Support both valid and invalid data

2. **Test Fixtures**
   - Setup/Teardown for each test
   - Clean test data between tests
   - Isolated test environment

### 4.3. Test Categories

1. **Unit Tests**
   - Test individual methods in isolation
   - Fast execution (< 1 second total)
   - No external dependencies

2. **Integration Tests**
   - Test multiple components together
   - Use real file system (temporary)
   - Test end-to-end flows

3. **Performance Tests**
   - Test with large datasets
   - Measure response times
   - Identify bottlenecks

## 5. Implementation Steps

### Phase 1: Setup (Week 1)
- [ ] Create test project
- [ ] Setup test framework (xUnit, NUnit, or MSTest)
- [ ] Add test dependencies (Moq, FluentAssertions)
- [ ] Create test helpers and builders
- [ ] Setup CI/CD for test execution

### Phase 2: Service Tests (Week 2)
- [ ] KeyService tests
- [ ] StorageService tests (all methods)
- [ ] Achieve 90%+ coverage on services

### Phase 3: Controller Tests (Week 3-4)
- [ ] UserController tests (highest priority)
- [ ] StoreNodeController tests
- [ ] BlockCreatorNodeController tests
- [ ] StoredFileController tests
- [ ] PolicyController tests

### Phase 4: Integration Tests (Week 5)
- [ ] End-to-end user registration flow
- [ ] End-to-end node registration flow
- [ ] End-to-end follow flow
- [ ] Test with real file system

### Phase 5: Refinement (Week 6)
- [ ] Review coverage reports
- [ ] Add missing test cases
- [ ] Performance testing
- [ ] Documentation

## 6. Test Tools & Libraries

### Recommended:
- **xUnit** - Test framework (modern, widely used)
- **Moq** - Mocking framework
- **FluentAssertions** - Readable assertions
- **AutoFixture** - Test data generation
- **Microsoft.AspNetCore.Mvc.Testing** - Integration testing
- **coverlet.collector** - Code coverage

### Alternative:
- **NUnit** - Alternative test framework
- **NSubstitute** - Alternative mocking framework
- **Shouldly** - Alternative assertion library

## 7. Test Data Examples

### Valid Test Data:
```csharp
// Valid User Registration
var validUser = new SignedRegisterRequest
{
    PublicKey = "02a1b2c3...",
    Email = "test@example.com",
    FullName = "Test User",
    DateOfBirth = new DateTime(1990, 1, 1),
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Signature = "valid_base64_signature"
};

// Valid Voucher
var validVoucher = new Voucher
{
    Code = "TEST-VOUCHER-001",
    Used = false
};

// Valid Node
var validNode = new StoreNode
{
    PublicKey = "02a1b2c3...",
    Endpoint = "http://localhost:5000",
    IsActive = true
};
```

## 8. Code Coverage Goals

| Component | Target Coverage | Priority |
|-----------|----------------|----------|
| KeyService | 95% | High |
| StorageService | 90% | High |
| UserController | 85% | High |
| StoreNodeController | 80% | Medium |
| BlockCreatorNodeController | 80% | Medium |
| StoredFileController | 75% | Medium |
| PolicyController | 70% | Low |

## 9. Continuous Integration

### CI Pipeline:
1. Run all unit tests on every commit
2. Run integration tests on pull requests
3. Generate coverage reports
4. Fail build if coverage drops below threshold
5. Publish test results and coverage

### Coverage Thresholds:
- Overall: 80%
- Critical paths: 95%
- New code: 90%

## 10. Best Practices

1. **Test Naming**: `MethodName_Scenario_ExpectedResult`
   - Example: `Register_ValidUser_ReturnsUserWithCentralSignature`

2. **Arrange-Act-Assert Pattern**:
   ```csharp
   // Arrange
   var user = TestDataBuilder.CreateUser();
   
   // Act
   var result = await controller.Register(user);
   
   // Assert
   result.Should().NotBeNull();
   ```

3. **One Assert Per Test** (when possible)
   - Makes tests easier to understand
   - Clear failure messages

4. **Test Isolation**:
   - Each test should be independent
   - No shared state between tests
   - Clean up after each test

5. **Test Real Behavior**:
   - Test what the code does, not how
   - Avoid over-mocking
   - Test edge cases and error conditions

## 11. Next Steps

1. Review and approve this test plan
2. Create test project structure
3. Start with KeyService tests (simplest)
4. Gradually add more tests
5. Integrate with CI/CD pipeline

## 12. Resources

- [xUnit Documentation](https://xunit.net/)
- [Moq Documentation](https://github.com/moq/moq4)
- [FluentAssertions Documentation](https://fluentassertions.com/)
- [ASP.NET Core Testing](https://docs.microsoft.com/en-us/aspnet/core/test/)










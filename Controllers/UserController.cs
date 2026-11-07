using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Models.Requests;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly KeyService _keyService;
        private readonly IStorageService _storageService;

        public class RegistrationResponse
        {
            public string Id { get; set; }
            public string Mnemonic { get; set; }
            public string PublicKey { get; set; }
        }

        public class UserSearchResponse
        {
            public List<DiscoveredUser> Users { get; set; } = new List<DiscoveredUser>();
        }

        public class FollowRequest
        {
            [JsonPropertyName("targetPublicKey")]
            public string TargetPublicKey { get; set; }
            
            [JsonPropertyName("signature")]
            public string Signature { get; set; }
            
            [JsonPropertyName("signedData")]
            public string SignedData { get; set; }
            
            [JsonPropertyName("timestamp")]
            public long Timestamp { get; set; }
        }

        public UserController(KeyService keyService, IStorageService storageService)
        {
            _keyService = keyService;
            _storageService = storageService;
        }

        [HttpGet("generateKeys")]
        public ActionResult<RegistrationResponse> GenerateKeys()
        {
            var (mnemonic, publicKey) = _keyService.GenerateKeyPair();

            return new RegistrationResponse
            {
                Id = System.Guid.NewGuid().ToString(),
                Mnemonic = mnemonic,
                PublicKey = publicKey
            };
        }

        [HttpPost("register")]
        public async Task<ActionResult<User>> Register([FromBody] SignedRegisterRequest request)
        {
            // Log các giá trị đầu vào
            Console.WriteLine($"[REGISTER] publicKey: {request.PublicKey}");
            Console.WriteLine($"[REGISTER] email: {request.Email}");
            Console.WriteLine($"[REGISTER] fullName: {request.FullName}");
            Console.WriteLine($"[REGISTER] dateOfBirth: {request.DateOfBirth}");
            Console.WriteLine($"[REGISTER] timestamp: {request.Timestamp}");
            Console.WriteLine($"[REGISTER] signature: {request.Signature}");

            // Validate email format
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains("@"))
            {
                return BadRequest("Invalid email format");
            }

            // Validate timestamp (should be within reasonable range)
            if (request.Timestamp <= 0)
            {
                return BadRequest("Invalid timestamp");
            }

            // Check timestamp is not too old or too far in future (allow 5 minutes window)
            var requestTime = DateTimeOffset.FromUnixTimeSeconds(request.Timestamp);
            var now = DateTimeOffset.UtcNow;
            var timeDiff = Math.Abs((now - requestTime).TotalMinutes);
            
            if (timeDiff > 5) // 5 minutes window
            {
                return BadRequest("Timestamp too old or too far in future");
            }

            // Check if email is already registered
            var users = await _storageService.GetUsersAsync();
            if (users.Any(u => u.Email == request.Email))
            {
                return BadRequest("Email already registered");
            }

            if (await _storageService.IsPublicKeyRegisteredAsync(request.PublicKey))
            {
                return BadRequest("Public key already registered");
            }

            // Verify signature with timestamp included
            var dobString = request.DateOfBirth?.ToString("yyyy-MM-dd") ?? "";
            var dataToVerify = $"{request.Email}:{request.FullName}:{dobString}:{request.Timestamp}";
            Console.WriteLine($"[REGISTER] Server computed dataToVerify: '{dataToVerify}'");
            Console.WriteLine($"[REGISTER] Server DoB string: '{dobString}' (from DateTime: {request.DateOfBirth})");
            Console.WriteLine($"[REGISTER] Server signature (base64): '{request.Signature}'");
            var verifyResult = _keyService.VerifySignature(request.PublicKey, dataToVerify, request.Signature);
            Console.WriteLine($"[REGISTER] verifyResult: {verifyResult}");
            if (!verifyResult)
            {
                return BadRequest("Invalid signature");
            }

            var user = new User
            {
                PublicKey = request.PublicKey,
                Email = request.Email,
                FullName = request.FullName,
                DateOfBirth = request.DateOfBirth,
                Bio = request.Bio,
                AvatarUrl = request.AvatarUrl,
                Signature = request.Signature, // Save signature for blockchain operations
                RegistrationTimestamp = request.Timestamp // Save registration timestamp
            };

            await _storageService.SaveUserAsync(user);

            // 🆕 Prepare central signature for user registration
            var centralSignature = SignByCentral(user);
            var centralPubKey = GetCentralPublicKeyOrEmpty();

            // 🆕 Broadcast registration transaction to Block Creator nodes
            _ = SendRegistrationToBlockCreatorsAsync(user, centralSignature, centralPubKey);

            return Ok(new
            {
                user,
                centralPubKey,
                centralSignature
            });
        }

        [HttpGet("verify/{publicKey}")]
        public async Task<ActionResult<bool>> VerifyRegistration(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
            {
                return BadRequest("Invalid public key format");
            }

            return await _storageService.IsPublicKeyRegisteredAsync(publicKey);
        }

        [HttpGet("profile/{publicKey}")]
        public async Task<ActionResult<User>> GetProfile(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
            {
                return BadRequest("Invalid public key format");
            }

            var users = await _storageService.GetUsersAsync();
            var user = users.FirstOrDefault(u => u.PublicKey == publicKey);
            
            if (user == null)
            {
                return NotFound("User not found");
            }

            return user;
        }

        [HttpPut("profile/{publicKey}")]
        public async Task<ActionResult<User>> UpdateProfile(string publicKey, [FromBody] SignedRegisterRequest request)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
            {
                return BadRequest("Invalid public key format");
            }

            var users = await _storageService.GetUsersAsync();
            var user = users.FirstOrDefault(u => u.PublicKey == publicKey);
            
            if (user == null)
            {
                return NotFound("User not found");
            }

            // Check if new email is already taken by another user
            if (request.Email != user.Email && users.Any(u => u.Email == request.Email))
            {
                return BadRequest("Email already taken");
            }

            // Verify signature
            var dataToVerify = $"{request.Email}:{request.FullName}:{request.DateOfBirth?.ToString("yyyy-MM-dd")}:{request.Timestamp}";
            if (!_keyService.VerifySignature(publicKey, dataToVerify, request.Signature))
            {
                return BadRequest("Invalid signature");
            }

            // Update user information
            user.Email = request.Email;
            user.FullName = request.FullName;
            user.DateOfBirth = request.DateOfBirth;
            user.Bio = request.Bio;
            user.AvatarUrl = request.AvatarUrl;
            user.Signature = request.Signature; // Update signature for blockchain operations
            user.RegistrationTimestamp = request.Timestamp; // Update registration timestamp
            user.UpdatedAt = DateTime.UtcNow;

            await _storageService.UpdateUserAsync(user);

            return user;
        }

        // User Discovery Endpoints

        [HttpGet("search")]
        public async Task<ActionResult<UserSearchResponse>> SearchUsers([FromQuery] string q, [FromHeader(Name = "X-User-PubKey")] string userPublicKey)
        {
            // Validate authentication
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            if (string.IsNullOrWhiteSpace(q))
            {
                return Ok(new UserSearchResponse());
            }

            var users = await _storageService.GetUsersAsync();
            var currentUserPublicKey = NormalizePublicKey(userPublicKey);

            // Search by display name or public key
            var searchResults = users.Where(u => 
                u.PublicKey != currentUserPublicKey && // Exclude current user
                ((!string.IsNullOrEmpty(u.FullName) && u.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                 u.PublicKey.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            var discoveredUsers = new List<DiscoveredUser>();
            
            foreach (var user in searchResults)
            {
                var followersCount = await _storageService.GetFollowersCountAsync(user.PublicKey);
                var followingCount = await _storageService.GetFollowingCountAsync(user.PublicKey);
                var isFollowing = await _storageService.IsFollowingAsync(currentUserPublicKey, user.PublicKey);

                discoveredUsers.Add(DiscoveredUser.FromUser(user, 0, followersCount, followingCount, isFollowing));
            }

            return Ok(new UserSearchResponse { Users = discoveredUsers });
        }

        [HttpGet("recent")]
        public async Task<ActionResult<UserSearchResponse>> GetRecentUsers([FromHeader(Name = "X-User-PubKey")] string userPublicKey, [FromQuery] int limit = 10)
        {
            // Validate authentication
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            var users = await _storageService.GetUsersAsync();
            var currentUserPublicKey = NormalizePublicKey(userPublicKey);

            // Get recent users, exclude current user
            var recentUsers = users
                .Where(u => u.PublicKey != currentUserPublicKey)
                .OrderByDescending(u => u.CreatedAt)
                .Take(limit)
                .ToList();

            var discoveredUsers = new List<DiscoveredUser>();
            
            foreach (var user in recentUsers)
            {
                var followersCount = await _storageService.GetFollowersCountAsync(user.PublicKey);
                var followingCount = await _storageService.GetFollowingCountAsync(user.PublicKey);
                var isFollowing = await _storageService.IsFollowingAsync(currentUserPublicKey, user.PublicKey);

                discoveredUsers.Add(DiscoveredUser.FromUser(user, 0, followersCount, followingCount, isFollowing));
            }

            return Ok(new UserSearchResponse { Users = discoveredUsers });
        }

        [HttpGet("following")]
        public async Task<ActionResult<UserSearchResponse>> GetFollowingUsers([FromHeader(Name = "X-User-PubKey")] string userPublicKey, [FromQuery] int limit = 10)
        {
            // Validate authentication
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            var currentUserPublicKey = NormalizePublicKey(userPublicKey);
            var followingPublicKeys = await _storageService.GetFollowingPublicKeysAsync(currentUserPublicKey);
            
            if (!followingPublicKeys.Any())
            {
                return Ok(new UserSearchResponse());
            }

            var users = await _storageService.GetUsersAsync();
            var followingUsers = users
                .Where(u => followingPublicKeys.Contains(u.PublicKey))
                .Take(limit)
                .ToList();

            var discoveredUsers = new List<DiscoveredUser>();
            
            foreach (var user in followingUsers)
            {
                var followersCount = await _storageService.GetFollowersCountAsync(user.PublicKey);
                var followingCount = await _storageService.GetFollowingCountAsync(user.PublicKey);

                discoveredUsers.Add(DiscoveredUser.FromUser(user, 0, followersCount, followingCount, true));
            }

            return Ok(new UserSearchResponse { Users = discoveredUsers });
        }

        [HttpPost("follow")]
        public async Task<ActionResult> FollowUser([FromHeader(Name = "X-User-PubKey")] string userPublicKey)
        {
            // Read body manually BEFORE validation
            string requestBodyJson = "";
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                Console.WriteLine($"[FOLLOW] Read body: '{requestBodyJson}'");
                
                // Store body in HttpContext for ValidateAuthentication to use
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FOLLOW] Error reading body: {ex.Message}");
                return BadRequest("Cannot read request body");
            }

            // Validate authentication
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            try
            {
                Console.WriteLine("[FOLLOW] Authentication successful, processing follow request...");

                // Parse body manually
                FollowRequest request;
                try
                {
                    Console.WriteLine($"[FOLLOW] Raw JSON to parse: '{requestBodyJson}'");
                    Console.WriteLine($"[FOLLOW] JSON length: {requestBodyJson.Length}");
                    
                    // Try to parse as generic object first for debugging
                    var rawData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBodyJson);
                    Console.WriteLine($"[FOLLOW] Raw data keys: {string.Join(", ", rawData.Keys)}");
                    foreach (var kvp in rawData)
                    {
                        Console.WriteLine($"[FOLLOW] {kvp.Key}: {kvp.Value} (type: {kvp.Value.GetType()})");
                    }
                    
                    // Manual parsing using JsonElement methods
                    request = new FollowRequest
                    {
                        TargetPublicKey = rawData["targetPublicKey"].GetString(),
                        Signature = rawData["signature"].GetString(),
                        SignedData = rawData["signedData"].GetString(),
                        Timestamp = rawData["timestamp"].GetInt64()
                    };
                    
                    Console.WriteLine($"[FOLLOW] Manually parsed request: targetPublicKey={request.TargetPublicKey}, timestamp={request.Timestamp}");
                    
                    if (request == null || string.IsNullOrEmpty(request.TargetPublicKey))
                    {
                        Console.WriteLine("[FOLLOW] Invalid request - null or empty targetPublicKey");
                        return BadRequest("Invalid request body");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FOLLOW] Error parsing JSON: {ex.Message}");
                    Console.WriteLine($"[FOLLOW] Stack trace: {ex.StackTrace}");
                    return BadRequest("Invalid JSON format");
                }

                var currentUserPublicKey = NormalizePublicKey(userPublicKey);
                var targetPublicKey = NormalizePublicKey(request.TargetPublicKey);

                Console.WriteLine($"[FOLLOW] Current user: {currentUserPublicKey}");
                Console.WriteLine($"[FOLLOW] Target user: {targetPublicKey}");

                // Validate target user exists
                Console.WriteLine("[FOLLOW] Checking if target user exists...");
                if (!await _storageService.IsPublicKeyRegisteredAsync(targetPublicKey))
                {
                    Console.WriteLine($"[FOLLOW] Target user not found: {targetPublicKey}");
                    return BadRequest("Target user not found");
                }

                Console.WriteLine($"[FOLLOW] Target user exists, attempting to follow...");
                
                // Verify signature on the data that client signed
                Console.WriteLine($"[FOLLOW] Signed data from client: {request.SignedData}");
                Console.WriteLine($"[FOLLOW] Signature from client: {request.Signature}");
                
                var signatureValid = _keyService.VerifySignature(currentUserPublicKey, request.SignedData, request.Signature);
                Console.WriteLine($"[FOLLOW] Signature verification result: {signatureValid}");
                
                if (!signatureValid)
                {
                    Console.WriteLine($"[FOLLOW] Invalid signature for follow action");
                    return BadRequest("Invalid signature for follow action");
                }
                
                // Parse signed data to validate it contains correct follow information
                try
                {
                    var signedRecord = JsonSerializer.Deserialize<Dictionary<string, object>>(request.SignedData);
                    var action = signedRecord["action"].ToString();
                    var followerKey = signedRecord["follower"].ToString();
                    var followingKey = signedRecord["following"].ToString();
                    var signedTimestamp = signedRecord["timestamp"].ToString();
                    
                    Console.WriteLine($"[FOLLOW] Signed action: {action}");
                    Console.WriteLine($"[FOLLOW] Signed follower: {followerKey}");
                    Console.WriteLine($"[FOLLOW] Signed following: {followingKey}");
                    Console.WriteLine($"[FOLLOW] Signed timestamp: {signedTimestamp}");
                    Console.WriteLine($"[FOLLOW] Request timestamp: {request.Timestamp}");
                    
                    // Validate the signed data matches the request
                    if (action != "follow" || followerKey != currentUserPublicKey || followingKey != targetPublicKey)
                    {
                        Console.WriteLine($"[FOLLOW] Signed data doesn't match request parameters");
                        return BadRequest("Signed data doesn't match request");
                    }
                    
                    // Validate timestamp matches (allowing small difference for network latency)
                    if (long.TryParse(signedTimestamp, out var parsedSignedTimestamp))
                    {
                        var timeDiff = Math.Abs(request.Timestamp - parsedSignedTimestamp);
                        if (timeDiff > 300) // Allow 5 minutes difference
                        {
                            Console.WriteLine($"[FOLLOW] Timestamp mismatch: signed={parsedSignedTimestamp}, request={request.Timestamp}, diff={timeDiff}s");
                            return BadRequest("Timestamp mismatch");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[FOLLOW] Invalid signed timestamp format: {signedTimestamp}");
                        return BadRequest("Invalid timestamp format");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FOLLOW] Error parsing signed data: {ex.Message}");
                    return BadRequest("Invalid signed data format");
                }
                
                // Now save the follow relationship with verified signature
                var success = await _storageService.FollowUserAsync(currentUserPublicKey, targetPublicKey, request.Signature, request.Timestamp);
                Console.WriteLine($"[FOLLOW] Follow result: {success}");
                
                if (!success)
                {
                    Console.WriteLine($"[FOLLOW] Follow failed - already following or trying to follow yourself");
                    return BadRequest("Cannot follow user (already following or trying to follow yourself)");
                }

                Console.WriteLine($"[FOLLOW] Successfully followed user");
                return Ok(new { message = "Successfully followed user" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FOLLOW] Unexpected error: {ex.Message}");
                Console.WriteLine($"[FOLLOW] Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpPost("unfollow")]
        public async Task<ActionResult> UnfollowUser([FromHeader(Name = "X-User-PubKey")] string userPublicKey)
        {
            // Read body manually BEFORE validation
            string requestBodyJson = "";
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                Console.WriteLine($"[UNFOLLOW] Read body: '{requestBodyJson}'");
                
                // Store body in HttpContext for ValidateAuthentication to use
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNFOLLOW] Error reading body: {ex.Message}");
                return BadRequest("Cannot read request body");
            }

            // Validate authentication
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            // Parse body manually
            FollowRequest request;
            try
            {
                request = JsonSerializer.Deserialize<FollowRequest>(requestBodyJson);
                if (request == null || string.IsNullOrEmpty(request.TargetPublicKey))
                {
                    return BadRequest("Invalid request body");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNFOLLOW] Error parsing JSON: {ex.Message}");
                return BadRequest("Invalid JSON format");
            }

            var currentUserPublicKey = NormalizePublicKey(userPublicKey);
            var targetPublicKey = NormalizePublicKey(request.TargetPublicKey);

            var success = await _storageService.UnfollowUserAsync(currentUserPublicKey, targetPublicKey);
            
            if (!success)
            {
                return BadRequest("Cannot unfollow user (not following)");
            }

            return Ok(new { message = "Successfully unfollowed user" });
        }

        [HttpGet("signatures")]
        public async Task<ActionResult<List<object>>> GetUsersWithSignatures()
        {
            var users = await _storageService.GetUsersAsync();
            
            var usersWithSignatures = users
                .Where(u => !string.IsNullOrEmpty(u.Signature)) // Only users with signatures
                .Select(u => new 
                {
                    Id = u.Id,
                    PublicKey = u.PublicKey,
                    Email = u.Email,
                    FullName = u.FullName,
                    DateOfBirth = u.DateOfBirth,
                    Signature = u.Signature,
                    RegistrationTimestamp = u.RegistrationTimestamp,
                    CreatedAt = u.CreatedAt
                })
                .ToList();
            
            return Ok(usersWithSignatures);
        }

        [HttpGet("followers")]
        public async Task<ActionResult<UserSearchResponse>> GetFollowers([FromHeader(Name = "X-User-PubKey")] string userPublicKey, [FromQuery] int limit = 100)
        {
            if (!await ValidateAuthentication())
            {
                return Unauthorized("Invalid authentication");
            }

            var currentUserPublicKey = NormalizePublicKey(userPublicKey);
            var followerPublicKeys = await _storageService.GetFollowersPublicKeysAsync(currentUserPublicKey);
            if (!followerPublicKeys.Any())
            {
                return Ok(new UserSearchResponse());
            }

            var users = await _storageService.GetUsersAsync();
            var followerUsers = users
                .Where(u => followerPublicKeys.Contains(u.PublicKey))
                .Take(limit)
                .ToList();

            var discoveredUsers = new List<DiscoveredUser>();
            foreach (var user in followerUsers)
            {
                var followersCount = await _storageService.GetFollowersCountAsync(user.PublicKey);
                var followingCount = await _storageService.GetFollowingCountAsync(user.PublicKey);
                var isFollowing = await _storageService.IsFollowingAsync(currentUserPublicKey, user.PublicKey);
                discoveredUsers.Add(DiscoveredUser.FromUser(user, 0, followersCount, followingCount, isFollowing));
            }

            return Ok(new UserSearchResponse { Users = discoveredUsers });
        }

        // Helper methods
        private async Task<bool> ValidateAuthentication()
        {
            Console.WriteLine("[AUTH] Starting authentication validation");
            
            // Extract authentication headers
            if (!Request.Headers.TryGetValue("X-User-PubKey", out var publicKeyHeader) ||
                !Request.Headers.TryGetValue("X-Timestamp", out var timestampHeader) ||
                !Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
            {
                Console.WriteLine("[AUTH] Missing headers");
                Console.WriteLine($"[AUTH] X-User-PubKey: {Request.Headers.ContainsKey("X-User-PubKey")}");
                Console.WriteLine($"[AUTH] X-Timestamp: {Request.Headers.ContainsKey("X-Timestamp")}");
                Console.WriteLine($"[AUTH] X-Signature: {Request.Headers.ContainsKey("X-Signature")}");
                return false;
            }

            var publicKey = publicKeyHeader.FirstOrDefault();
            var timestamp = timestampHeader.FirstOrDefault();
            var signature = signatureHeader.FirstOrDefault();

            Console.WriteLine($"[AUTH] Raw publicKey: {publicKey}");
            Console.WriteLine($"[AUTH] Timestamp: {timestamp}");
            Console.WriteLine($"[AUTH] Signature: {signature}");

            if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            {
                Console.WriteLine("[AUTH] Empty headers");
                return false;
            }

            // Read request body FIRST for POST requests
            string requestBody = "";
            if (Request.Method == "POST")
            {
                Console.WriteLine($"[AUTH] ContentLength: {Request.ContentLength}");
                Console.WriteLine($"[AUTH] ContentType: {Request.ContentType}");
                
                // Try to get cached body from HttpContext.Items (set by endpoint)
                if (HttpContext.Items.TryGetValue("RequestBody", out var cachedBody))
                {
                    requestBody = cachedBody?.ToString() ?? "";
                    Console.WriteLine($"[AUTH] Using cached body: '{requestBody}'");
                }
                else
                {
                    Console.WriteLine("[AUTH] No cached body found, body may have been consumed by model binding");
                    requestBody = ""; // Use empty string for hash calculation
                }
                
                Console.WriteLine($"[AUTH] Request body length: {requestBody.Length}");
            }

            // Store original public key for signature verification
            var originalPublicKey = publicKey;
            Console.WriteLine($"[AUTH] Original publicKey: {originalPublicKey}");

            // Check if user is registered with BOTH compressed and uncompressed formats
            var isRegistered = false;
            var keyForSignatureVerification = originalPublicKey;

            // Try original key first (could be compressed or uncompressed)
            if (await _storageService.IsPublicKeyRegisteredAsync(originalPublicKey))
            {
                isRegistered = true;
                keyForSignatureVerification = originalPublicKey;
                Console.WriteLine($"[AUTH] User found with original key format: {originalPublicKey}");
            }
            else
            {
                // If original key is compressed, try uncompressed version
                if (originalPublicKey.Length == 66 && (originalPublicKey.StartsWith("02") || originalPublicKey.StartsWith("03")))
                {
                    var uncompressedKey = NormalizePublicKey(originalPublicKey);
                    Console.WriteLine($"[AUTH] Trying uncompressed version: {uncompressedKey}");
                    
                    if (await _storageService.IsPublicKeyRegisteredAsync(uncompressedKey))
                    {
                        isRegistered = true;
                        keyForSignatureVerification = originalPublicKey; // Keep original for signature verification
                        Console.WriteLine($"[AUTH] User found with uncompressed key: {uncompressedKey}");
                    }
                }
                // If original key is uncompressed, try compressed version
                else if (originalPublicKey.Length == 130 && originalPublicKey.StartsWith("04"))
                {
                    try
                    {
                        var compressedKey = new NBitcoin.PubKey(originalPublicKey).ToString();
                        Console.WriteLine($"[AUTH] Trying compressed version: {compressedKey}");
                        
                        if (await _storageService.IsPublicKeyRegisteredAsync(compressedKey))
                        {
                            isRegistered = true;
                            keyForSignatureVerification = originalPublicKey; // Keep original for signature verification
                            Console.WriteLine($"[AUTH] User found with compressed key: {compressedKey}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AUTH] Error converting to compressed key: {ex.Message}");
                    }
                }
            }

            if (!isRegistered)
            {
                Console.WriteLine($"[AUTH] Public key not registered in any format: {originalPublicKey}");
                return false;
            }

            // Validate timestamp (allow 5 minutes window)
            if (!long.TryParse(timestamp, out var timestampLong))
            {
                Console.WriteLine($"[AUTH] Invalid timestamp format: {timestamp}");
                return false;
            }

            var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampLong);
            var now = DateTimeOffset.UtcNow;
            var timeDiff = Math.Abs((now - requestTime).TotalMinutes);
            
            Console.WriteLine($"[AUTH] Request time: {requestTime}");
            Console.WriteLine($"[AUTH] Current time: {now}");
            Console.WriteLine($"[AUTH] Time difference: {timeDiff} minutes");
            
            if (timeDiff > 5) // 5 minutes window
            {
                Console.WriteLine($"[AUTH] Timestamp too old: {timeDiff} minutes");
                return false;
            }

            // Create signature data
            var method = Request.Method;
            var path = Request.Path + Request.QueryString;
            var bodyHash = "";

            Console.WriteLine($"[AUTH] Method: {method}");
            Console.WriteLine($"[AUTH] Path: {path}");

            // For POST requests, hash the body (always for POST, even if empty)
            if (Request.Method == "POST")
            {
                using var sha256 = SHA256.Create();
                var bodyBytes = Encoding.UTF8.GetBytes(requestBody); // Will be empty string if no body
                var hashBytes = sha256.ComputeHash(bodyBytes);
                bodyHash = Convert.ToHexString(hashBytes).ToLower();
                Console.WriteLine($"[AUTH] Body hash: {bodyHash}");
            }

            var dataToSign = $"{method}{path}{timestamp}{bodyHash}";
            Console.WriteLine($"[AUTH] Data to sign: {dataToSign}");

            // Verify signature using the key format found in database
            var verificationResult = _keyService.VerifySignature(keyForSignatureVerification, dataToSign, signature);
            Console.WriteLine($"[AUTH] Signature verification result: {verificationResult}");
            Console.WriteLine($"[AUTH] Used key for verification: {keyForSignatureVerification}");
            
            return verificationResult;
        }

        /// <summary>
        /// Get new followers since a specific timestamp
        /// Used by author A to sync and generate rekeys for new followers
        /// </summary>
        [HttpGet("new-followers")]
        public async Task<ActionResult> GetNewFollowers([FromHeader(Name = "X-User-PubKey")] string userPublicKey, [FromQuery] long? sinceTimestamp)
        {
            try
            {
                // Validate authentication
                if (!await ValidateAuthentication())
                {
                    return Unauthorized("Invalid authentication");
                }

                // Default to last 24 hours if no timestamp provided
                DateTime since = sinceTimestamp.HasValue 
                    ? DateTimeOffset.FromUnixTimeSeconds(sinceTimestamp.Value).UtcDateTime
                    : DateTime.UtcNow.AddHours(-24);

                var newFollows = await _storageService.GetNewFollowersSinceAsync(userPublicKey, since);

                var response = newFollows.Select(f => new
                {
                    followerPubKey = f.FollowerPublicKey,
                    createdAt = f.CreatedAt,
                    timestamp = new DateTimeOffset(f.CreatedAt).ToUnixTimeSeconds()
                }).ToList();

                return Ok(new 
                { 
                    count = response.Count,
                    followers = response,
                    since = new DateTimeOffset(since).ToUnixTimeSeconds()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NEW_FOLLOWERS] Error: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all followers who need rekeys for a specific tag
        /// Used by author A to know which followers to generate rekeys for
        /// </summary>
        [HttpGet("followers-without-rekey")]
        public async Task<ActionResult> GetFollowersWithoutRekey(
            [FromHeader(Name = "X-User-PubKey")] string userPublicKey, 
            [FromQuery] string tag)
        {
            try
            {
                // Validate authentication
                if (!await ValidateAuthentication())
                {
                    return Unauthorized("Invalid authentication");
                }

                if (string.IsNullOrWhiteSpace(tag))
                {
                    return BadRequest("Tag is required");
                }

                var followersWithoutRekey = await _storageService.GetFollowersWithoutRekeyAsync(userPublicKey, tag);

                return Ok(new 
                { 
                    tag,
                    count = followersWithoutRekey.Count,
                    followers = followersWithoutRekey
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FOLLOWERS_WITHOUT_REKEY] Error: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        private string NormalizePublicKey(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
                return publicKey;

            // If it's compressed (66 chars), convert to uncompressed (130 chars)
            if (publicKey.Length == 66 && (publicKey.StartsWith("02") || publicKey.StartsWith("03")))
            {
                try
                {
                    var compressedKey = new NBitcoin.PubKey(publicKey);
                    return compressedKey.ToHex();
                }
                catch
                {
                    return publicKey;
                }
            }

            return publicKey;
        }

        /// <summary>
        /// Send user registration transaction to all active Block Creator nodes
        /// Fire-and-forget; failures are logged to console without blocking the request
        /// </summary>
        private async Task SendRegistrationToBlockCreatorsAsync(User user, string centralSignature, string centralPubKey)
        {
            try
            {
                var blockers = await _storageService.GetActiveBlockCreatorNodesAsync();
                if (blockers == null || blockers.Count == 0)
                {
                    Console.WriteLine("[BLOCKER_TX] No active Block Creator nodes found.");
                    return;
                }

                var payload = new
                {
                    type = "user_registration",
                    publicKey = user.PublicKey,
                    email = user.Email,
                    fullName = user.FullName,
                    timestamp = user.RegistrationTimestamp ?? 0,
                    signature = user.Signature,
                    centralPubKey,
                    centralSignature
                };

                using var http = new System.Net.Http.HttpClient();

                foreach (var node in blockers)
                {
                    try
                    {
                        var endpoint = node.Endpoint?.TrimEnd('/') ?? "";
                        if (string.IsNullOrEmpty(endpoint))
                            continue;

                        var url = $"{endpoint}/api/blockcreator/transactions";
                        var json = System.Text.Json.JsonSerializer.Serialize(payload);
                        var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                        var resp = await http.PostAsync(url, content);
                        Console.WriteLine($"[BLOCKER_TX] Sent registration to {endpoint}, status={resp.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BLOCKER_TX] Error sending to node {node.Endpoint}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLOCKER_TX] Unexpected error broadcasting to Block Creator nodes: {ex.Message}");
            }
        }

        private string SignByCentral(User user)
        {
            try
            {
                var centralKey = GetCentralPrivateKey();
                if (centralKey == null)
                {
                    Console.WriteLine("[BLOCKER_TX] Central private key not set; skipping central signature.");
                    return string.Empty;
                }

                var message = $"user_registration|{user.PublicKey}|{user.Email}|{user.FullName}|{user.RegistrationTimestamp ?? 0}";
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message));
                var sig = centralKey.Sign(new uint256(hash)).ToDER();
                return Convert.ToBase64String(sig);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLOCKER_TX] Error signing by central: {ex.Message}");
                return string.Empty;
            }
        }

        private string GetCentralPublicKeyOrEmpty()
        {
            var key = GetCentralPrivateKey();
            return key?.PubKey.ToHex() ?? string.Empty;
        }

        private Key GetCentralPrivateKey()
        {
            // Expect CENTRAL_PRIVATE_KEY as hex (32 bytes) or WIF
            var env = Environment.GetEnvironmentVariable("CENTRAL_PRIVATE_KEY");
            if (string.IsNullOrWhiteSpace(env))
                return null;

            try
            {
                if (env.StartsWith("K") || env.StartsWith("L") || env.StartsWith("5"))
                {
                    return Key.Parse(env, Network.Main);
                }
                // assume hex
                var bytes = NBitcoin.DataEncoders.Encoders.Hex.DecodeData(env);
                return new Key(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLOCKER_TX] Failed to parse CENTRAL_PRIVATE_KEY: {ex.Message}");
                return null;
            }
        }
    }
} 
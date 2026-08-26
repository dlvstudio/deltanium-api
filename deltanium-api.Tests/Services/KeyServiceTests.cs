using FluentAssertions;
using Xunit;
using DeltaniumApi.Services;
using deltanium_api.Tests.Helpers;
using NBitcoin;
using System.Security.Cryptography;
using System.Text;

namespace deltanium_api.Tests.Services
{
    public class KeyServiceTests
    {
        private readonly KeyService _keyService;

        public KeyServiceTests()
        {
            _keyService = new KeyService();
        }

        [Fact]
        public void GenerateKeyPair_ShouldReturnValidMnemonic()
        {
            // Act
            var (mnemonic, publicKey) = _keyService.GenerateKeyPair();

            // Assert
            mnemonic.Should().NotBeNullOrEmpty();
            var words = mnemonic.Split(' ');
            words.Length.Should().Be(12, "BIP39 mnemonic should have 12 words");
        }

        [Fact]
        public void GenerateKeyPair_ShouldReturnValidPublicKey()
        {
            // Act
            var (mnemonic, publicKey) = _keyService.GenerateKeyPair();

            // Assert
            publicKey.Should().NotBeNullOrEmpty();
            _keyService.ValidatePublicKey(publicKey).Should().BeTrue();
        }

        [Fact]
        public void GenerateKeyPair_ShouldReturnDifferentKeysOnEachCall()
        {
            // Act
            var (mnemonic1, publicKey1) = _keyService.GenerateKeyPair();
            var (mnemonic2, publicKey2) = _keyService.GenerateKeyPair();

            // Assert
            mnemonic1.Should().NotBe(mnemonic2);
            publicKey1.Should().NotBe(publicKey2);
        }

        [Fact]
        public void GenerateKeyPair_MnemonicShouldDeriveSameKeyPair()
        {
            // Act
            var (mnemonic, publicKey1) = _keyService.GenerateKeyPair();
            
            // Derive key pair from mnemonic
            var m = new Mnemonic(mnemonic);
            var hdRoot = m.DeriveExtKey();
            var privateKey = hdRoot.PrivateKey;
            var publicKey2 = privateKey.PubKey.ToString();

            // Assert
            publicKey1.Should().Be(publicKey2);
        }

        [Fact]
        public void ValidatePublicKey_ValidCompressedPublicKey_ShouldReturnTrue()
        {
            // Arrange
            var publicKey = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = _keyService.ValidatePublicKey(publicKey);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void ValidatePublicKey_ValidUncompressedPublicKey_ShouldReturnTrue()
        {
            // Arrange
            var publicKey = TestDataBuilder.CreateUncompressedPublicKey();

            // Act
            var result = _keyService.ValidatePublicKey(publicKey);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void ValidatePublicKey_Null_ShouldReturnFalse()
        {
            // Act
            var result = _keyService.ValidatePublicKey(null!);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void ValidatePublicKey_EmptyString_ShouldReturnFalse()
        {
            // Act
            var result = _keyService.ValidatePublicKey(string.Empty);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void ValidatePublicKey_WrongLength_ShouldReturnFalse()
        {
            // Arrange
            var invalidKey = "02" + new string('a', 60); // 62 chars, should be 66

            // Act
            var result = _keyService.ValidatePublicKey(invalidKey);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void ValidatePublicKey_InvalidHexCharacters_ShouldReturnFalse()
        {
            // Arrange
            var invalidKey = "02" + new string('x', 64); // Contains invalid hex chars

            // Act
            var result = _keyService.ValidatePublicKey(invalidKey);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void ValidatePublicKey_WrongPrefix_ShouldReturnFalse()
        {
            // Arrange
            var invalidKey = "01" + new string('a', 64); // Wrong prefix (should be 02, 03, or 04)

            // Act
            var result = _keyService.ValidatePublicKey(invalidKey);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void VerifySignature_ValidSignature_ShouldReturnTrue()
        {
            // Arrange
            var (mnemonic, publicKey) = TestDataBuilder.GenerateKeyPair();
            var message = "test message";
            
            // Create signature
            var m = new Mnemonic(mnemonic);
            var privateKey = m.DeriveExtKey().PrivateKey;
            using var sha256 = SHA256.Create();
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var hashBytes = sha256.ComputeHash(messageBytes);
            var hash = new uint256(hashBytes);
            var signature = privateKey.Sign(hash);
            var signatureBase64 = Convert.ToBase64String(signature.ToDER());

            // Act
            var result = _keyService.VerifySignature(publicKey, message, signatureBase64);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void VerifySignature_WrongKey_ShouldReturnFalse()
        {
            // Arrange
            var (_, publicKey1) = TestDataBuilder.GenerateKeyPair();
            var (mnemonic2, _) = TestDataBuilder.GenerateKeyPair();
            var message = "test message";
            
            // Create signature with key2 but verify with key1
            var m = new Mnemonic(mnemonic2);
            var privateKey = m.DeriveExtKey().PrivateKey;
            using var sha256 = SHA256.Create();
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var hashBytes = sha256.ComputeHash(messageBytes);
            var hash = new uint256(hashBytes);
            var signature = privateKey.Sign(hash);
            var signatureBase64 = Convert.ToBase64String(signature.ToDER());

            // Act
            var result = _keyService.VerifySignature(publicKey1, message, signatureBase64);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void VerifySignature_WrongMessage_ShouldReturnFalse()
        {
            // Arrange
            var (mnemonic, publicKey) = TestDataBuilder.GenerateKeyPair();
            var message1 = "test message 1";
            var message2 = "test message 2";
            
            // Create signature for message1
            var m = new Mnemonic(mnemonic);
            var privateKey = m.DeriveExtKey().PrivateKey;
            using var sha256 = SHA256.Create();
            var messageBytes = Encoding.UTF8.GetBytes(message1);
            var hashBytes = sha256.ComputeHash(messageBytes);
            var hash = new uint256(hashBytes);
            var signature = privateKey.Sign(hash);
            var signatureBase64 = Convert.ToBase64String(signature.ToDER());

            // Act - verify with message2
            var result = _keyService.VerifySignature(publicKey, message2, signatureBase64);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void VerifySignature_InvalidSignatureFormat_ShouldReturnFalse()
        {
            // Arrange
            var (_, publicKey) = TestDataBuilder.GenerateKeyPair();
            var message = "test message";
            var invalidSignature = "not_a_valid_base64_signature!!!";

            // Act
            var result = _keyService.VerifySignature(publicKey, message, invalidSignature);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void VerifySignature_EmptyMessage_ShouldHandleGracefully()
        {
            // Arrange
            var (mnemonic, publicKey) = TestDataBuilder.GenerateKeyPair();
            var message = "";
            
            // Create signature for empty message
            var m = new Mnemonic(mnemonic);
            var privateKey = m.DeriveExtKey().PrivateKey;
            using var sha256 = SHA256.Create();
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var hashBytes = sha256.ComputeHash(messageBytes);
            var hash = new uint256(hashBytes);
            var signature = privateKey.Sign(hash);
            var signatureBase64 = Convert.ToBase64String(signature.ToDER());

            // Act
            var result = _keyService.VerifySignature(publicKey, message, signatureBase64);

            // Assert
            result.Should().BeTrue(); // Empty message should still verify correctly
        }
    }
}










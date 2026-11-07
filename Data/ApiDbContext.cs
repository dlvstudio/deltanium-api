using System;
using System.Collections.Generic;
using DeltaniumApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using NewtonsoftJson = Newtonsoft.Json.JsonConvert;

namespace DeltaniumApi.Data
{
    public class ApiDbContext : DbContext
    {
        public ApiDbContext(DbContextOptions<ApiDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<StoreNode> StoreNodes => Set<StoreNode>();
        public DbSet<BlockCreatorNode> BlockCreatorNodes => Set<BlockCreatorNode>();
        public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
        public DbSet<Voucher> Vouchers => Set<Voucher>();
        public DbSet<UserFollow> UserFollows => Set<UserFollow>();
        public DbSet<UserRekey> UserRekeys => Set<UserRekey>();
        public DbSet<Conversation> Conversations => Set<Conversation>();
        public DbSet<ChatMessageMetadata> ChatMessages => Set<ChatMessageMetadata>();
        public DbSet<OnlineSession> OnlineSessions => Set<OnlineSession>();
        public DbSet<SignalingMessage> SignalingMessages => Set<SignalingMessage>();
        public DbSet<TransactionEnvelopeRecord> TransactionEnvelopes => Set<TransactionEnvelopeRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(e =>
            {
                e.ToTable("users");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.PublicKey).IsUnique();
                // secp256k1 hex keys are ≤130 chars; keep headroom under MySQL utf8mb4 index limits
                e.Property(x => x.PublicKey).HasMaxLength(130).IsRequired();
                e.Property(x => x.Email).HasMaxLength(320).IsRequired(false);
                e.Property(x => x.FullName).HasMaxLength(256).IsRequired(false);
                e.Property(x => x.Bio).IsRequired(false);
                e.Property(x => x.AvatarUrl).IsRequired(false);
                e.Property(x => x.Signature).IsRequired(false);
            });

            modelBuilder.Entity<StoreNode>(e =>
            {
                e.ToTable("store_nodes");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.PublicKey).IsUnique();
                e.Property(x => x.PublicKey).HasMaxLength(130).IsRequired();
                e.Property(x => x.Endpoint).HasMaxLength(512);
            });

            modelBuilder.Entity<BlockCreatorNode>(e =>
            {
                e.ToTable("block_creator_nodes");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.PublicKey).IsUnique();
                e.Property(x => x.PublicKey).HasMaxLength(130).IsRequired();
                e.Property(x => x.Endpoint).HasMaxLength(512);
            });

            modelBuilder.Entity<StoredFile>(e =>
            {
                e.ToTable("stored_files");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.FileId);
                e.HasIndex(x => new { x.OwnerPublicKey, x.StoreNodeId });
                e.Property(x => x.FileId).HasMaxLength(64);
                e.Property(x => x.OwnerPublicKey).HasMaxLength(130);
                e.Property(x => x.StoreNodeId).HasMaxLength(64);
                e.Property(x => x.FileHash).HasMaxLength(128);
            });

            modelBuilder.Entity<Voucher>(e =>
            {
                e.ToTable("vouchers");
                e.HasKey(x => x.Code);
                e.Property(x => x.Code).HasMaxLength(128);
            });

            modelBuilder.Entity<UserFollow>(e =>
            {
                e.ToTable("user_follows");
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.FollowerPublicKey, x.FollowingPublicKey }).IsUnique();
                e.Property(x => x.FollowerPublicKey).HasMaxLength(130);
                e.Property(x => x.FollowingPublicKey).HasMaxLength(130);
                e.Property(x => x.Signature).HasMaxLength(1024).IsRequired(false);
            });

            modelBuilder.Entity<UserRekey>(e =>
            {
                e.ToTable("user_rekeys");
                e.HasKey(x => x.Id);
                // Avoid oversized composite indexes under utf8mb4 (max ~3072 bytes)
                e.HasIndex(x => new { x.FollowingPubKey, x.FollowerPubKey, x.Tag });
                e.Property(x => x.FollowingPubKey).HasMaxLength(130);
                e.Property(x => x.FollowerPubKey).HasMaxLength(130);
                e.Property(x => x.Tag).HasMaxLength(64);
                e.Property(x => x.Scope).HasMaxLength(32);
                e.Property(x => x.FileId).HasMaxLength(64);
                e.Property(x => x.RkBase64).HasColumnType("longtext");
            });

            modelBuilder.Entity<Conversation>(e =>
            {
                e.ToTable("conversations");
                e.HasKey(x => x.ConversationId);
                e.Property(x => x.ConversationId).HasMaxLength(64);
                e.Property(x => x.Participants)
                    .HasConversion(
                        v => NewtonsoftJson.SerializeObject(v),
                        v => NewtonsoftJson.DeserializeObject<List<string>>(v) ?? new List<string>())
                    .HasColumnType("longtext")
                    .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                        (a, b) => NewtonsoftJson.SerializeObject(a) == NewtonsoftJson.SerializeObject(b),
                        v => NewtonsoftJson.SerializeObject(v).GetHashCode(),
                        v => NewtonsoftJson.DeserializeObject<List<string>>(NewtonsoftJson.SerializeObject(v)) ?? new List<string>()));
            });

            modelBuilder.Entity<ChatMessageMetadata>(e =>
            {
                e.ToTable("chat_messages");
                e.HasKey(x => x.MessageId);
                e.HasIndex(x => x.ConversationId);
                e.HasIndex(x => new { x.RecipientPubKey, x.IsRead });
                e.Property(x => x.MessageId).HasMaxLength(64);
                e.Property(x => x.ClientMessageId).HasMaxLength(64);
                e.Property(x => x.ConversationId).HasMaxLength(64);
                e.Property(x => x.SenderPubKey).HasMaxLength(130);
                e.Property(x => x.RecipientPubKey).HasMaxLength(130);
                e.Property(x => x.StoreNodeId).HasMaxLength(64);
                e.Property(x => x.BlockId).HasMaxLength(64);
                e.Property(x => x.FileId).HasMaxLength(64);
                e.Property(x => x.RecipientFileId).HasMaxLength(64);
            });

            modelBuilder.Entity<OnlineSession>(e =>
            {
                e.ToTable("online_sessions");
                e.HasKey(x => x.UserPubKey);
                e.Property(x => x.UserPubKey).HasMaxLength(130);
                e.Property(x => x.SignalingToken).HasMaxLength(512);
            });

            modelBuilder.Entity<SignalingMessage>(e =>
            {
                e.ToTable("signaling_messages");
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.ToPubKey, x.IsConsumed });
                e.Property(x => x.Id).HasMaxLength(64);
                e.Property(x => x.FromPubKey).HasMaxLength(130);
                e.Property(x => x.ToPubKey).HasMaxLength(130);
                e.Property(x => x.Type).HasMaxLength(32);
                e.Property(x => x.Payload).HasColumnType("longtext");
            });

            modelBuilder.Entity<TransactionEnvelopeRecord>(e =>
            {
                e.ToTable("transaction_envelopes");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Delivered);
                e.Property(x => x.Id).HasMaxLength(64);
                e.Property(x => x.TransactionJson).HasColumnType("longtext").IsRequired();
            });
        }
    }

    /// <summary>
    /// Persistence shape for transaction envelopes (nested BlockchainTransaction stored as JSON).
    /// </summary>
    public class TransactionEnvelopeRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TransactionJson { get; set; } = "{}";
        public bool Delivered { get; set; }
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }

        public TransactionEnvelope ToModel()
        {
            return new TransactionEnvelope
            {
                Id = Id,
                Transaction = NewtonsoftJson.DeserializeObject<BlockchainTransaction>(TransactionJson) ?? new BlockchainTransaction(),
                Delivered = Delivered,
                ReceivedAt = ReceivedAt,
                DeliveredAt = DeliveredAt
            };
        }

        public static TransactionEnvelopeRecord FromModel(TransactionEnvelope envelope)
        {
            return new TransactionEnvelopeRecord
            {
                Id = string.IsNullOrWhiteSpace(envelope.Id) ? Guid.NewGuid().ToString("N") : envelope.Id,
                TransactionJson = NewtonsoftJson.SerializeObject(envelope.Transaction),
                Delivered = envelope.Delivered,
                ReceivedAt = envelope.ReceivedAt,
                DeliveredAt = envelope.DeliveredAt
            };
        }

        public static TransactionEnvelopeRecord FromTransaction(BlockchainTransaction tx)
        {
            return new TransactionEnvelopeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TransactionJson = NewtonsoftJson.SerializeObject(tx),
                Delivered = false,
                ReceivedAt = DateTime.UtcNow
            };
        }
    }
}

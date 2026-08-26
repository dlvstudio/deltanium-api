using System;

namespace DeltaniumApi.Models
{
    public class DiscoveredUser
    {
        public string PublicKey { get; set; }
        public string DisplayName { get; set; }
        public string Bio { get; set; }
        public string AvatarUrl { get; set; }
        public DateTime JoinDate { get; set; }
        public int PostsCount { get; set; }
        public int FollowersCount { get; set; }
        public int FollowingCount { get; set; }
        public bool IsFollowing { get; set; }
        public DateTime? LastActive { get; set; }

        public static DiscoveredUser FromUser(User user, int postsCount = 0, int followersCount = 0, int followingCount = 0, bool isFollowing = false)
        {
            return new DiscoveredUser
            {
                PublicKey = user.PublicKey,
                DisplayName = user.FullName,
                Bio = user.Bio,
                AvatarUrl = user.AvatarUrl,
                JoinDate = user.CreatedAt,
                PostsCount = postsCount,
                FollowersCount = followersCount,
                FollowingCount = followingCount,
                IsFollowing = isFollowing,
                LastActive = user.UpdatedAt
            };
        }
    }
} 
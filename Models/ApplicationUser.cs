using Microsoft.AspNetCore.Identity;

namespace RestaurantERP.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string FullName { get; set; } = string.Empty;
        public string FullNameAr { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsActive { get; set; } = true;
        public string? ProfileImage { get; set; }
        // The branch this user currently operates from (can be overridden per session)
        public int? DefaultBranchId { get; set; }
        public Branch? DefaultBranch { get; set; }
        public ICollection<UserBranch> UserBranches { get; set; } = new List<UserBranch>();
    }
}
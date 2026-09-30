using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;

namespace RestaurantERP.Services
{
    /// <summary>
    /// Resolves which branch the current user is operating from.
    /// Priority: session override → user default branch → first assigned branch → main branch → branch 1
    /// </summary>
    public class BranchService
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHttpContextAccessor _http;
        private const string SessionKey = "CurrentBranchId";

        public BranchService(ApplicationDbContext context,
                             UserManager<ApplicationUser> userManager,
                             IHttpContextAccessor http)
        {
            _context = context;
            _userManager = userManager;
            _http = http;
        }

        // ── Get current branch ID ─────────────────────────────────
        public async Task<int> GetCurrentBranchIdAsync()
        {
            var ctx = _http.HttpContext;
            if (ctx == null) return 1;

            // 1. Session override (branch switcher)
            var sessionBranchId = ctx.Session.GetInt32(SessionKey);
            if (sessionBranchId.HasValue) return sessionBranchId.Value;

            // 2. Logged-in user's default branch
            var user = await _userManager.GetUserAsync(ctx.User) as ApplicationUser;
            if (user?.DefaultBranchId != null) return user.DefaultBranchId.Value;

            // 3. First branch assigned to this user
            if (user != null)
            {
                var ub = await _context.UserBranches
                    .Where(x => x.UserId == user.Id)
                    .OrderByDescending(x => x.IsPrimary)
                    .FirstOrDefaultAsync();
                if (ub != null) return ub.BranchId;
            }

            // 4. Main branch
            var main = await _context.Branches.FirstOrDefaultAsync(b => b.IsMainBranch && b.IsActive);
            if (main != null) return main.Id;

            // 5. Any active branch
            var any = await _context.Branches.FirstOrDefaultAsync(b => b.IsActive);
            return any?.Id ?? 1;
        }

        public async Task<Branch?> GetCurrentBranchAsync()
        {
            var id = await GetCurrentBranchIdAsync();
            return await _context.Branches.FindAsync(id);
        }

        // ── Switch branch (stores in session) ────────────────────
        public void SetBranch(int branchId)
        {
            _http.HttpContext?.Session.SetInt32(SessionKey, branchId);
        }

        // ── Get all branches the user can access ──────────────────
        public async Task<List<Branch>> GetAccessibleBranchesAsync()
        {
            var ctx = _http.HttpContext;
            var user = ctx != null ? await _userManager.GetUserAsync(ctx.User) : null;

            // Admin sees all branches
            if (ctx != null && ctx.User.IsInRole("Admin"))
                return await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();

            // Others see only assigned branches
            if (user == null) return new List<Branch>();
            var branchIds = await _context.UserBranches
                .Where(ub => ub.UserId == user.Id)
                .Select(ub => ub.BranchId)
                .ToListAsync();
            return await _context.Branches
                .Where(b => branchIds.Contains(b.Id) && b.IsActive)
                .OrderBy(b => b.Name)
                .ToListAsync();
        }

        // ── Assign user to branch ─────────────────────────────────
        public async Task AssignUserToBranchAsync(string userId, int branchId, bool isPrimary = false)
        {
            var existing = await _context.UserBranches
                .FirstOrDefaultAsync(ub => ub.UserId == userId && ub.BranchId == branchId);
            if (existing != null) { existing.IsPrimary = isPrimary; }
            else
            {
                _context.UserBranches.Add(new UserBranch { UserId = userId, BranchId = branchId, IsPrimary = isPrimary });
            }

            if (isPrimary)
            {
                // Clear other primary flags for this user
                var others = await _context.UserBranches
                    .Where(ub => ub.UserId == userId && ub.BranchId != branchId && ub.IsPrimary)
                    .ToListAsync();
                others.ForEach(o => o.IsPrimary = false);

                // Update user's default branch
                var user = await _context.Users.FindAsync(userId) as ApplicationUser;
                if (user != null) { user.DefaultBranchId = branchId; _context.Update(user); }
            }
            await _context.SaveChangesAsync();
        }
    }
}
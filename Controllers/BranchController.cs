using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize]
    public class BranchController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly BranchService _branchService;

        public BranchController(ApplicationDbContext context,
                                UserManager<ApplicationUser> userManager,
                                BranchService branchService)
        {
            _context = context;
            _userManager = userManager;
            _branchService = branchService;
        }

        // ── Admin: Branches List ──────────────────────────────────
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Index()
        {
            var branches = await _context.Branches
                .Include(b => b.Manager)
                .Include(b => b.UserBranches)
                .Include(b => b.Orders)
                .ToListAsync();
            return View(branches);
        }

        // ── Admin: Branch Analytics Dashboard ─────────────────────
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> Analytics()
        {
            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);

            var completedStatuses = new[] {
                OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund
            };

            var branches = await _context.Branches
                .Where(b => b.IsActive)
                .Include(b => b.Manager)
                .ToListAsync();

            var branchStats = new List<BranchStatDto>();
            foreach (var branch in branches)
            {
                var todayOrders = await _context.Orders
                    .Where(o => o.BranchId == branch.Id && o.CreatedAt.Date == today
                             && completedStatuses.Contains(o.Status)).ToListAsync();

                var monthOrders = await _context.Orders
                    .Where(o => o.BranchId == branch.Id && o.CreatedAt >= thisMonth
                             && completedStatuses.Contains(o.Status)).ToListAsync();

                var todayRefunds = await _context.Refunds
                    .Where(r => r.BranchId == branch.Id && r.CreatedAt.Date == today
                             && r.Status == RefundStatus.Completed).SumAsync(r => r.RefundTotal);

                var monthRefunds = await _context.Refunds
                    .Where(r => r.BranchId == branch.Id && r.CreatedAt >= thisMonth
                             && r.Status == RefundStatus.Completed).SumAsync(r => r.RefundTotal);

                var monthExpenses = await _context.Expenses
                    .Where(e => e.BranchId == branch.Id && e.Date >= thisMonth).SumAsync(e => e.Amount);

                var activeTables = await _context.DiningTables
                    .CountAsync(t => t.BranchId == branch.Id && t.Status == TableStatus.Occupied);

                var pendingOrders = await _context.Orders
                    .CountAsync(o => o.BranchId == branch.Id &&
                               (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Preparing));

                var activeShifts = await _context.Shifts
                    .CountAsync(s => s.BranchId == branch.Id && !s.IsClosed);

                var staffCount = await _context.UserBranches
                    .CountAsync(ub => ub.BranchId == branch.Id);

                branchStats.Add(new BranchStatDto
                {
                    Branch = branch,
                    TodaySales = Math.Max(0, todayOrders.Sum(o => o.Total) - todayRefunds),
                    TodayOrders = todayOrders.Count,
                    MonthSales = Math.Max(0, monthOrders.Sum(o => o.Total) - monthRefunds),
                    MonthOrders = monthOrders.Count,
                    MonthExpenses = monthExpenses,
                    MonthProfit = Math.Max(0, monthOrders.Sum(o => o.Total) - monthRefunds) - monthExpenses,
                    ActiveTables = activeTables,
                    PendingOrders = pendingOrders,
                    ActiveShifts = activeShifts,
                    StaffCount = staffCount
                });
            }

            return View(branchStats);
        }

        // ── Admin: Create Branch ──────────────────────────────────
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create()
        {
            ViewBag.Managers = await _userManager.GetUsersInRoleAsync("Manager");
            return View();
        }

        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create([FromBody] Branch branch)
        {
            if (!_context.Branches.Any()) branch.IsMainBranch = true;
            _context.Branches.Add(branch);
            await _context.SaveChangesAsync();

            // Seed default settings for the new branch
            var globalSettings = await _context.SystemSettings
                .Where(s => s.BranchId == null).ToListAsync();
            foreach (var gs in globalSettings)
            {
                _context.SystemSettings.Add(new SystemSettings
                {
                    Key = gs.Key,
                    Value = gs.Value,
                    BranchId = branch.Id
                });
            }
            await _context.SaveChangesAsync();

            return Json(new { success = true, branchId = branch.Id, message = $"Branch '{branch.Name}' created" });
        }

        // ── Admin: Edit Branch ────────────────────────────────────
        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit([FromBody] Branch incoming)
        {
            var branch = await _context.Branches.FindAsync(incoming.Id);
            if (branch == null) return Json(new { success = false });

            branch.Name = incoming.Name;
            branch.NameAr = incoming.NameAr;
            branch.Address = incoming.Address;
            branch.Phone = incoming.Phone;
            branch.Email = incoming.Email;
            branch.ManagerId = incoming.ManagerId;
            branch.ColorHex = incoming.ColorHex;
            branch.Icon = incoming.Icon;
            branch.IsActive = incoming.IsActive;

            if (incoming.IsMainBranch)
            {
                await _context.Branches.ForEachAsync(b => b.IsMainBranch = false);
                branch.IsMainBranch = true;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ── Admin: Toggle Branch Active ───────────────────────────
        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> Toggle([FromBody] IdRequest req)
        {
            var branch = await _context.Branches.FindAsync(req.Id);
            if (branch == null) return Json(new { success = false });
            if (branch.IsMainBranch) return Json(new { success = false, message = "Cannot deactivate main branch" });
            branch.IsActive = !branch.IsActive;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isActive = branch.IsActive });
        }

        // ── Admin: Assign / remove user from branch ───────────────
        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> AssignUser([FromBody] AssignUserRequest req)
        {
            try
            {
                await _branchService.AssignUserToBranchAsync(req.UserId, req.BranchId, req.IsPrimary);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> RemoveUser([FromBody] AssignUserRequest req)
        {
            var ub = await _context.UserBranches
                .FirstOrDefaultAsync(x => x.UserId == req.UserId && x.BranchId == req.BranchId);
            if (ub == null) return Json(new { success = false });
            _context.UserBranches.Remove(ub);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ── Switch Branch (any logged-in user) ────────────────────
        [HttpPost]
        public async Task<IActionResult> Switch([FromBody] SwitchBranchRequest req)
        {
            // Verify user has access to this branch
            var accessible = await _branchService.GetAccessibleBranchesAsync();
            if (!accessible.Any(b => b.Id == req.BranchId))
                return Json(new { success = false, message = "Access denied to this branch" });

            _branchService.SetBranch(req.BranchId);
            var branch = accessible.First(b => b.Id == req.BranchId);
            return Json(new { success = true, branchName = branch.Name, branchNameAr = branch.NameAr });
        }

        // ── API: Get accessible branches for switcher dropdown ─────
        [HttpGet]
        public async Task<IActionResult> GetAccessible()
        {
            var branches = await _branchService.GetAccessibleBranchesAsync();
            var current = await _branchService.GetCurrentBranchIdAsync();
            return Json(new
            {
                current,
                branches = branches.Select(b => new {
                    b.Id,
                    b.Name,
                    b.NameAr,
                    b.ColorHex,
                    b.Icon,
                    b.IsMainBranch,
                    isCurrent = b.Id == current
                })
            });
        }

        // ── API: Branch users for management ──────────────────────
        [HttpGet, Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetBranchUsers(int branchId)
        {
            var assigned = await _context.UserBranches
                .Include(ub => ub.User)
                .Where(ub => ub.BranchId == branchId)
                .ToListAsync();

            var allUsers = await _userManager.Users.ToListAsync();
            var assignedIds = assigned.Select(ub => ub.UserId).ToHashSet();

            return Json(new
            {
                assigned = assigned.Select(ub => new
                {
                    ub.UserId,
                    userName = ub.User?.UserName,
                    fullName = ub.User?.FullName,
                    ub.IsPrimary
                }),
                unassigned = allUsers
                    .Where(u => !assignedIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName, u.FullName })
            });
        }

        // ── API: Cross-branch revenue chart ───────────────────────
        [HttpGet, Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetCrossAnalytics(int days = 30)
        {
            var from = DateTime.Today.AddDays(-days);
            var completedStatuses = new[] {
                OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund
            };

            var branches = await _context.Branches.Where(b => b.IsActive).ToListAsync();

            var data = new List<object>();
            foreach (var b in branches)
            {
                var dailyRevenue = await _context.Orders
                    .Where(o => o.BranchId == b.Id && o.CreatedAt >= from
                             && completedStatuses.Contains(o.Status))
                    .GroupBy(o => o.CreatedAt.Date)
                    .Select(g => new { date = g.Key.ToString("MM/dd"), revenue = g.Sum(o => o.Total) })
                    .OrderBy(x => x.date)
                    .ToListAsync();

                var totalRev = dailyRevenue.Sum(x => x.revenue);
                var totalRefunds = await _context.Refunds
                    .Where(r => r.BranchId == b.Id && r.CreatedAt >= from && r.Status == RefundStatus.Completed)
                    .SumAsync(r => r.RefundTotal);

                data.Add(new
                {
                    branchId = b.Id,
                    branchName = b.Name,
                    branchNameAr = b.NameAr,
                    colorHex = b.ColorHex,
                    netRevenue = Math.Max(0, totalRev - totalRefunds),
                    dailyRevenue
                });
            }

            return Json(data);
        }

        // ── Admin: Delete Branch ─────────────────────────────────
        [HttpPost, Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete([FromBody] IdRequest req)
        {
            var branch = await _context.Branches.FindAsync(req.Id);
            if (branch == null)
                return Json(new { success = false, message = "الفرع غير موجود" });

            if (branch.IsMainBranch)
                return Json(new { success = false, message = "لا يمكن حذف الفرع الرئيسي. عيّن فرعاً آخر كرئيسي أولاً." });

            var hasOrders = await _context.Orders.AnyAsync(o => o.BranchId == req.Id);
            if (hasOrders)
                return Json(new { success = false, message = "لا يمكن حذف الفرع لأنه يحتوي على طلبات مسجلة. يمكنك تعطيله بدلاً من ذلك." });

            // Remove related data
            var userBranches = await _context.UserBranches.Where(ub => ub.BranchId == req.Id).ToListAsync();
            var productBranches = await _context.ProductBranches.Where(pb => pb.BranchId == req.Id).ToListAsync();
            var settings = await _context.SystemSettings.Where(s => s.BranchId == req.Id).ToListAsync();

            _context.UserBranches.RemoveRange(userBranches);
            _context.ProductBranches.RemoveRange(productBranches);
            _context.SystemSettings.RemoveRange(settings);

            // Move expenses to main branch instead of losing them
            var mainId = await _context.Branches
                .Where(b => b.IsMainBranch && b.Id != req.Id)
                .Select(b => b.Id).FirstOrDefaultAsync();
            if (mainId > 0)
            {
                var expenses = await _context.Expenses.Where(e => e.BranchId == req.Id).ToListAsync();
                expenses.ForEach(e => e.BranchId = mainId);
                var shifts = await _context.Shifts.Where(s => s.BranchId == req.Id).ToListAsync();
                shifts.ForEach(s => s.BranchId = mainId);
            }

            _context.Branches.Remove(branch);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
    }

    // ── Request models ────────────────────────────────────────────
    public class IdRequest { public int Id { get; set; } }
    public class SwitchBranchRequest { public int BranchId { get; set; } }
    public class AssignUserRequest
    {
        public string UserId { get; set; } = string.Empty;
        public int BranchId { get; set; }
        public bool IsPrimary { get; set; }
    }

    // ── DTOs ──────────────────────────────────────────────────────
    public class BranchStatDto
    {
        public Branch Branch { get; set; } = null!;
        public decimal TodaySales { get; set; }
        public int TodayOrders { get; set; }
        public decimal MonthSales { get; set; }
        public int MonthOrders { get; set; }
        public decimal MonthExpenses { get; set; }
        public decimal MonthProfit { get; set; }
        public int ActiveTables { get; set; }
        public int PendingOrders { get; set; }
        public int ActiveShifts { get; set; }
        public int StaffCount { get; set; }
    }
}
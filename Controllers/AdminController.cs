using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize(Roles = "Admin,Manager,محصل")]
    public class AdminController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly AnalyticsService _analytics;
        public AdminController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            AnalyticsService analytics)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
            _analytics = analytics;
        }

        private async Task<int> GetBranchIdAsync()
        {
            var userId = _userManager.GetUserId(User);
            var user = await _context.Users
                .OfType<ApplicationUser>()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user?.DefaultBranchId != null)
                return user.DefaultBranchId.Value;
            var userBranch = await _context.UserBranches
                .Where(ub => ub.UserId == userId)
                .Select(ub => ub.BranchId)
                .FirstOrDefaultAsync();
            if (userBranch > 0) return userBranch;
            return await _context.Branches
                .Where(b => b.IsMainBranch)
                .Select(b => b.Id)
                .FirstOrDefaultAsync();
        }

        // ═══════════════════════════════════════════════════
        // DASHBOARD
        // ═══════════════════════════════════════════════════
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> Index(int? branchId)
        {
            var branches = await _context.Branches
                .Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            var selectedBranch = branchId.HasValue
                ? branches.FirstOrDefault(b => b.Id == branchId) : null;

            ViewBag.Branches = branches;
            ViewBag.SelectedBranch = selectedBranch;
            ViewBag.SelectedBranchId = branchId;

            var stats = await _analytics.GetDashboardStatsAsync(branchId);
            return View(stats);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetStats(int? branchId)
        {
            var stats = await _analytics.GetDashboardStatsAsync(branchId);
            return Json(stats);
        }

        // ═══════════════════════════════════════════════════
        // PRODUCTS
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Products(string? search, int? categoryId, bool? active, int? branchId)
        {
            var query = _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches).ThenInclude(pb => pb.Branch)
                .AsQueryable();

            if (!string.IsNullOrEmpty(search))
                query = query.Where(p => p.Name.Contains(search) || p.NameAr.Contains(search));
            if (categoryId.HasValue)
                query = query.Where(p => p.CategoryId == categoryId);
            if (active.HasValue)
                query = query.Where(p => p.IsActive == active);
            if (branchId.HasValue)
                query = query.Where(p => p.ProductBranches.Any(pb => pb.BranchId == branchId));

            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Branches = branches;
            ViewBag.BranchId = branchId;
            ViewBag.Search = search;
            ViewBag.CategoryId = categoryId;
            return View(await query.OrderByDescending(p => p.CreatedAt).ToListAsync());
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateProduct(int? branchId)
        {
            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Branches = branches;
            ViewBag.DefaultBranchId = branchId
                ?? await _context.Branches.Where(b => b.IsMainBranch).Select(b => (int?)b.Id).FirstOrDefaultAsync();
            // Pass an instance, not null: with a null model the asp-for tag helpers
            // render empty values and every bool posts back as false.
            return View(new Product());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateProduct(Product model, [FromForm] List<int> selectedBranchIds)
        {
            if (string.IsNullOrEmpty(model.Name)) model.Name = model.NameAr;
            if (model.SellByBox && model.UnitsPerBox > 0)
            {
                model.Price = Math.Round(model.BoxSellPrice / model.UnitsPerBox, 2);
                model.CostPrice = Math.Round(model.BoxCostPrice / model.UnitsPerBox, 2);
            }
            ModelState.Clear();
            if (string.IsNullOrWhiteSpace(model.NameAr))
                return Json(new { success = false, message = "اسم الصنف مطلوب" });
            if (model.CategoryId <= 0)
                return Json(new { success = false, message = "اختر الفئة" });

            // NOTE: no price check here any more. A product is created as bare master
            // data; the selling price is set per branch on the distribution screen,
            // and the cost price is calculated from supply receipts.

            model.Barcode = model.Barcode ?? string.Empty;
            model.Name = string.IsNullOrEmpty(model.Name) ? model.NameAr : model.Name;
            model.NameAr = model.NameAr ?? string.Empty;
            model.BoxBarcode = model.BoxBarcode ?? string.Empty;
            model.CreatedAt = DateTime.Now;

            // Availability is decided per branch at distribution time. Keep the
            // product-level flag on so it never silently hides everywhere.
            model.IsAvailable = true;

            // The warehouse ledger tracks every item, so this is never optional.
            // Opening stock is always 0 — quantity arrives via supply receipts.
            model.TrackStock = true;
            model.StockQuantity = 0;
            if (string.IsNullOrWhiteSpace(model.Unit)) model.Unit = "قطعة";
            if (model.UnitsPerPackage <= 0) model.UnitsPerPackage = 1;

            _context.Products.Add(model);
            await _context.SaveChangesAsync();

            _context.InventoryLogs.Add(new InventoryLog
            {
                ProductId = model.Id,
                QuantityChange = model.StockQuantity,
                QuantityBefore = 0,
                QuantityAfter = model.StockQuantity,
                Reason = "رصيد افتتاحي — منتج جديد",
                Type = InventoryLogType.NewProduct,
                UnitCostAtEvent = model.CostPrice,
                MovementValue = model.StockQuantity * model.CostPrice,
                CreatedAt = DateTime.Now,
                CreatedById = _userManager.GetUserId(User)
            });

            // Deliberately NOT creating ProductBranch rows here. A new item is master
            // data only; it reaches a branch (with its price, tax and stock) through the
            // distribution screen. Creating empty rows would make it show up on cashier
            // grids with zero stock and no price.
            foreach (var bid in selectedBranchIds.Distinct())
                _context.ProductBranches.Add(new ProductBranch { ProductId = model.Id, BranchId = bid, IsAvailable = false });
            await _context.SaveChangesAsync();

            TempData["Success"] = "تم إضافة الصنف. دلوقتي سجّل له وارد من ايصالات التوريد، وبعدين وزّعه على الفروع وحدد سعره.";
            return RedirectToAction(nameof(Products));
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> EditProduct(int id)
        {
            var product = await _context.Products
                .Include(p => p.ProductBranches)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.SelectedBranchIds = product.ProductBranches.Select(pb => pb.BranchId).ToList();
            return View(product);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProduct(int id, Product model, [FromForm] List<int> selectedBranchIds)
        {
            if (id != model.Id) return NotFound();
            if (string.IsNullOrEmpty(model.Name)) model.Name = model.NameAr;
            if (model.SellByBox && model.UnitsPerBox > 0)
            {
                model.Price = Math.Round(model.BoxSellPrice / model.UnitsPerBox, 2);
                model.CostPrice = Math.Round(model.BoxCostPrice / model.UnitsPerBox, 2);
            }
            ModelState.Clear();
            model.Barcode = model.Barcode ?? string.Empty;
            model.Name = string.IsNullOrEmpty(model.Name) ? model.NameAr : model.Name;
            model.NameAr = model.NameAr ?? string.Empty;
            model.BoxBarcode = model.BoxBarcode ?? string.Empty;
            model.UpdatedAt = DateTime.Now;
            _context.Update(model);

            var existingBranches = await _context.ProductBranches.Where(pb => pb.ProductId == id).ToListAsync();
            _context.ProductBranches.RemoveRange(existingBranches);
            if (!selectedBranchIds.Any())
            {
                var mainId = await _context.Branches.Where(b => b.IsMainBranch).Select(b => b.Id).FirstOrDefaultAsync();
                if (mainId > 0) selectedBranchIds.Add(mainId);
            }
            foreach (var bid in selectedBranchIds.Distinct())
                _context.ProductBranches.Add(new ProductBranch { ProductId = id, BranchId = bid });

            await _context.SaveChangesAsync();
            TempData["Success"] = "تم تحديث المنتج بنجاح!";
            return RedirectToAction(nameof(Products));
        }

        [HttpPost]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return Json(new { success = false });
            var hasOrders = await _context.OrderItems.AnyAsync(oi => oi.ProductId == id);
            if (hasOrders) { product.IsActive = false; _context.Update(product); }
            else _context.Products.Remove(product);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return Json(new { success = false });
            product.IsAvailable = !product.IsAvailable;
            await _context.SaveChangesAsync();
            return Json(new { success = true, isAvailable = product.IsAvailable });
        }

        // ═══════════════════════════════════════════════════
        // CATEGORIES
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Categories()
        {
            var cats = await _context.Categories.Include(c => c.Products).ToListAsync();
            return View(cats);
        }

        [HttpPost]
        public async Task<IActionResult> SaveCategory([FromBody] Category model)
        {
            if (model == null) return Json(new { success = false, message = "بيانات غير صحيحة" });
            if (model.Id == 0) { model.CreatedAt = DateTime.Now; _context.Categories.Add(model); }
            else _context.Update(model);
            await _context.SaveChangesAsync();
            return Json(new { success = true, id = model.Id });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            var cat = await _context.Categories.FindAsync(id);
            if (cat == null) return Json(new { success = false });
            var hasProducts = await _context.Products.AnyAsync(p => p.CategoryId == id);
            if (hasProducts) return Json(new { success = false, message = "لا يمكن حذف فئة بها منتجات" });
            _context.Categories.Remove(cat);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ═══════════════════════════════════════════════════
        // USERS
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Users()
        {
            var users = await _userManager.Users
                .Include(u => u.DefaultBranch)
                .Include(u => u.UserBranches).ThenInclude(ub => ub.Branch)
                .ToListAsync();

            var userWithRoles = new List<(ApplicationUser User, IList<string> Roles, List<Branch> Branches)>();
            foreach (var user in users)
            {
                var roles = await _userManager.GetRolesAsync(user);
                var branches = user.UserBranches?
                    .Select(ub => ub.Branch!).Where(b => b != null).ToList() ?? new List<Branch>();
                userWithRoles.Add((user, roles, branches));
            }
            ViewBag.Roles = await _roleManager.Roles.Select(r => r.Name).ToListAsync();
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            return View(userWithRoles);
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
        {
            if (req == null) return Json(new { success = false, message = "بيانات غير صحيحة" });
            var user = new ApplicationUser
            {
                UserName = req.Email,
                Email = req.Email,
                FullName = req.FullName,
                FullNameAr = req.FullNameAr,
                EmailConfirmed = true,
                IsActive = true,
                DefaultBranchId = req.PrimaryBranchId > 0 ? req.PrimaryBranchId
                                : req.BranchIds?.FirstOrDefault() > 0 ? req.BranchIds.First() : null
            };
            var result = await _userManager.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Json(new { success = false, message = string.Join(", ", result.Errors.Select(e => e.Description)) });
            await _userManager.AddToRoleAsync(user, req.Role);
            if (req.BranchIds != null && req.BranchIds.Any())
            {
                var primaryId = req.PrimaryBranchId ?? req.BranchIds.First();
                foreach (var bid in req.BranchIds)
                    _context.UserBranches.Add(new UserBranch { UserId = user.Id, BranchId = bid, IsPrimary = bid == primaryId });
                await _context.SaveChangesAsync();
            }
            return Json(new { success = true, userId = user.Id });
        }

        [HttpPost]
        public async Task<IActionResult> EditUser([FromBody] EditUserRequest req)
        {
            if (req == null) return Json(new { success = false, message = "بيانات غير صحيحة" });
            var user = await _userManager.FindByIdAsync(req.UserId) as ApplicationUser;
            if (user == null) return Json(new { success = false, message = "المستخدم غير موجود" });
            user.FullName = req.FullName;
            user.FullNameAr = req.FullNameAr;
            if (!string.IsNullOrEmpty(req.Email) && req.Email != user.Email)
            { user.Email = req.Email; user.UserName = req.Email; }
            await _userManager.UpdateAsync(user);
            var currentRoles = await _userManager.GetRolesAsync(user);
            if (!string.IsNullOrEmpty(req.Role) && !currentRoles.Contains(req.Role))
            {
                await _userManager.RemoveFromRolesAsync(user, currentRoles);
                await _userManager.AddToRoleAsync(user, req.Role);
            }
            return Json(new { success = true });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteUser([FromBody] DeleteByIdStringRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.Id))
                return Json(new { success = false, message = "معرف المستخدم مطلوب" });
            var user = await _userManager.FindByIdAsync(req.Id);
            if (user == null) return Json(new { success = false, message = "المستخدم غير موجود" });
            if (user.Id == _userManager.GetUserId(User))
                return Json(new { success = false, message = "لا يمكنك حذف حسابك الخاص" });

            var userBranches = await _context.UserBranches.Where(ub => ub.UserId == user.Id).ToListAsync();
            _context.UserBranches.RemoveRange(userBranches);
            var orders = await _context.Orders.Where(o => o.CashierId == user.Id).ToListAsync();
            orders.ForEach(o => o.CashierId = null);
            var expenses = await _context.Expenses.Where(e => e.CreatedById == user.Id || e.RecordedById == user.Id).ToListAsync();
            expenses.ForEach(e => { if (e.CreatedById == user.Id) e.CreatedById = null; if (e.RecordedById == user.Id) e.RecordedById = null; });
            var shifts = await _context.Shifts.Where(s => s.UserId == user.Id).ToListAsync();
            shifts.ForEach(s => s.UserId = string.Empty);
            if (_context.Refunds != null)
            {
                var refunds = await _context.Refunds.Where(r => r.ProcessedById == user.Id).ToListAsync();
                refunds.ForEach(r => r.ProcessedById = null);
            }
            var managedBranches = await _context.Branches.Where(b => b.ManagerId == user.Id).ToListAsync();
            managedBranches.ForEach(b => b.ManagerId = null);
            await _context.SaveChangesAsync();

            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
                return Json(new { success = false, message = string.Join(", ", result.Errors.Select(e => e.Description)) });
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleUser([FromBody] ToggleUserRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.UserId)) return Json(new { success = false });
            var user = await _userManager.FindByIdAsync(req.UserId);
            if (user == null) return Json(new { success = false });
            user.IsActive = !user.IsActive;
            await _userManager.UpdateAsync(user);
            return Json(new { success = true, isActive = user.IsActive });
        }

        [HttpPost]
        public async Task<IActionResult> ResetUserPassword([FromBody] ResetPasswordRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.UserId))
                return Json(new { success = false, message = "بيانات غير صحيحة" });
            var user = await _userManager.FindByIdAsync(req.UserId);
            if (user == null) return Json(new { success = false, message = "المستخدم غير موجود" });
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, req.NewPassword);
            return Json(new { success = result.Succeeded, message = result.Succeeded ? "تم إعادة تعيين كلمة المرور" : string.Join(", ", result.Errors.Select(e => e.Description)) });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateUserBranches([FromBody] UpdateUserBranchesRequest req)
        {
            if (req == null) return Json(new { success = false });
            var existing = await _context.UserBranches.Where(ub => ub.UserId == req.UserId).ToListAsync();
            _context.UserBranches.RemoveRange(existing);
            var primaryId = req.PrimaryBranchId ?? req.BranchIds.FirstOrDefault();
            foreach (var bid in req.BranchIds)
                _context.UserBranches.Add(new UserBranch { UserId = req.UserId, BranchId = bid, IsPrimary = bid == primaryId });
            var user = await _userManager.FindByIdAsync(req.UserId) as ApplicationUser;
            if (user != null)
            {
                user.DefaultBranchId = req.BranchIds.Contains(primaryId) ? primaryId : req.BranchIds.FirstOrDefault();
                await _userManager.UpdateAsync(user);
            }
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ═══════════════════════════════════════════════════
        // ORDERS
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Orders(DateTime? from, DateTime? to, string? status)
        {
            from ??= DateTime.Today.AddDays(-30);
            to ??= DateTime.Today;
            var query = _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Table).Include(o => o.Cashier)
                .Where(o => o.CreatedAt.Date >= from.Value.Date && o.CreatedAt.Date <= to.Value.Date);
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<OrderStatus>(status, out var s))
                query = query.Where(o => o.Status == s);
            ViewBag.From = from.Value.ToString("yyyy-MM-dd");
            ViewBag.To = to.Value.ToString("yyyy-MM-dd");
            ViewBag.Status = status;
            return View(await query.OrderByDescending(o => o.CreatedAt).ToListAsync());
        }

        public async Task<IActionResult> OrderDetails(int id)
        {
            var order = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Table).Include(o => o.Cashier)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            return View(order);
        }

        // ═══════════════════════════════════════════════════
        // TABLES  ← WITH BRANCH FILTER
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Tables(int? branchId)
        {
            var query = _context.DiningTables
                .Include(t => t.Branch)
                .Include(t => t.Orders.Where(o =>
                    o.Status == OrderStatus.Pending || o.Status == OrderStatus.Preparing))
                .AsQueryable();

            if (branchId.HasValue)
                query = query.Where(t => t.BranchId == branchId);

            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.Branches = branches;
            ViewBag.BranchId = branchId;

            return View(await query.OrderBy(t => t.TableNumber).ToListAsync());
        }

        public class SaveTableDto
        {
            public int Id { get; set; }
            public int TableNumber { get; set; }
            public int Capacity { get; set; } = 4;
            public string? Section { get; set; }
            public int BranchId { get; set; }
            public string Status { get; set; } = "Available";
        }

        [HttpPost]
        public async Task<IActionResult> SaveTable([FromBody] SaveTableDto dto)
        {
            if (dto == null || dto.TableNumber < 1)
                return Json(new { success = false, message = "رقم الطاولة مطلوب" });

            if (dto.BranchId < 1)
                return Json(new { success = false, message = "يرجى اختيار الفرع" });

            if (!Enum.TryParse<TableStatus>(dto.Status, out var status))
                status = TableStatus.Available;

            if (dto.Id == 0)
            {
                _context.DiningTables.Add(new DiningTable
                {
                    TableNumber = dto.TableNumber.ToString(),
                    Capacity = dto.Capacity,
                    Section = dto.Section,
                    BranchId = dto.BranchId,
                    Status = status
                });
            }
            else
            {
                var table = await _context.DiningTables.FindAsync(dto.Id);
                if (table == null) return Json(new { success = false, message = "الطاولة غير موجودة" });
                table.TableNumber = dto.TableNumber.ToString();
                table.Capacity = dto.Capacity;
                table.Section = dto.Section;
                table.BranchId = dto.BranchId;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTable([FromBody] DeleteByIdRequest req)
        {
            var table = await _context.DiningTables.FindAsync(req.Id);
            if (table == null) return Json(new { success = false, message = "الطاولة غير موجودة" });

            var hasActiveOrders = await _context.Orders
                .AnyAsync(o => o.TableId == req.Id &&
                          (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Preparing));
            if (hasActiveOrders)
                return Json(new { success = false, message = "لا يمكن حذف طاولة عليها طلبات نشطة" });

            // Detach completed orders
            var orders = await _context.Orders.Where(o => o.TableId == req.Id).ToListAsync();
            orders.ForEach(o => o.TableId = null);

            _context.DiningTables.Remove(table);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateTableStatus([FromBody] UpdateTableStatusRequest req)
        {
            var table = await _context.DiningTables.FindAsync(req.TableId);
            if (table == null) return Json(new { success = false, message = "الطاولة غير موجودة" });
            if (Enum.TryParse<TableStatus>(req.Status, out var status))
            {
                table.Status = status;
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            return Json(new { success = false, message = "حالة غير صحيحة" });
        }

        // ═══════════════════════════════════════════════════
        // EXPENSES  ← WITH BRANCH FILTER & BRANCH SELECTOR
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Expenses(DateTime? from, DateTime? to, int? branchId)
        {
            from ??= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            to ??= DateTime.Today;

            var query = _context.Expenses
                .Include(e => e.CreatedBy)
                .Include(e => e.Branch)
                .Where(e => e.Date.Date >= from.Value.Date && e.Date.Date <= to.Value.Date);

            if (branchId.HasValue)
                query = query.Where(e => e.BranchId == branchId);

            var expenses = await query.OrderByDescending(e => e.Date).ToListAsync();
            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();

            ViewBag.From = from.Value.ToString("yyyy-MM-dd");
            ViewBag.To = to.Value.ToString("yyyy-MM-dd");
            ViewBag.Total = expenses.Sum(e => e.Amount);
            ViewBag.Branches = branches;
            ViewBag.BranchId = branchId;
            return View(expenses);
        }

        [HttpPost]
        public async Task<IActionResult> SaveExpense([FromBody] SaveExpenseDto dto)
        {
            if (dto == null)
                return Json(new { success = false, message = "بيانات غير صحيحة" });
            if (string.IsNullOrWhiteSpace(dto.Title))
                return Json(new { success = false, message = "يرجى إدخال عنوان المصروف" });
            if (dto.Amount <= 0)
                return Json(new { success = false, message = "يرجى إدخال مبلغ صحيح" });

            var defaultBranchId = await GetBranchIdAsync();
            var expenseBranchId = (dto.BranchId.HasValue && dto.BranchId.Value > 0)
                                   ? dto.BranchId.Value : defaultBranchId;
            var userId = _userManager.GetUserId(User);

            if (dto.Id == 0)
            {
                _context.Expenses.Add(new Expense
                {
                    Title = dto.Title,
                    Description = dto.Description,
                    Amount = dto.Amount,
                    Category = dto.Category ?? "",
                    PaymentMethod = dto.PaymentMethod,
                    Notes = dto.Notes,
                    Date = dto.Date == default ? DateTime.Now : dto.Date,
                    BranchId = expenseBranchId,
                    CreatedById = userId,
                    RecordedById = userId
                });
            }
            else
            {
                var expense = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == dto.Id);
                if (expense == null) return Json(new { success = false, message = "المصروف غير موجود" });
                expense.Title = dto.Title;
                expense.Description = dto.Description;
                expense.Amount = dto.Amount;
                expense.Category = dto.Category ?? "";
                expense.PaymentMethod = dto.PaymentMethod;
                expense.Notes = dto.Notes;
                expense.Date = dto.Date == default ? expense.Date : dto.Date;
            }
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteExpense([FromBody] DeleteByIdRequest req)
        {
            var expense = await _context.Expenses.FindAsync(req.Id);
            if (expense == null) return Json(new { success = false });
            _context.Expenses.Remove(expense);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ═══════════════════════════════════════════════════
        // INVENTORY
        // ═══════════════════════════════════════════════════
        public IActionResult Inventory()
        {
            // Superseded by the warehouse module, which keeps a proper ledger.
            // Kept as a redirect so old bookmarks/links still work.
            return RedirectToAction("Dashboard", "Inventory");
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStock([FromBody] UpdateStockRequest req)
        {
            var product = await _context.Products.FindAsync(req.ProductId);
            if (product == null) return Json(new { success = false });
            var before = product.StockQuantity;
            product.StockQuantity += req.Quantity;
            product.TrackStock = true;
            _context.InventoryLogs.Add(new InventoryLog
            {
                ProductId = req.ProductId,
                QuantityChange = req.Quantity,
                QuantityBefore = before,
                QuantityAfter = product.StockQuantity,
                Reason = req.Reason,
                Type = InventoryLogType.Adjustment,
                CreatedAt = DateTime.Now,
                CreatedById = _userManager.GetUserId(User)
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true, newStock = product.StockQuantity });
        }

        // ═══════════════════════════════════════════════════
        // DAILY REPORT — exact sales, refunds & movements for one chosen day
        // ═══════════════════════════════════════════════════
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> DailyReport(DateTime? date, int? branchId)
        {
            var day = (date ?? DateTime.Today).Date;
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.BranchId = branchId;

            var completedStatuses = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };

            var ordersQuery = _context.Orders
                .Include(o => o.Items)
                .Include(o => o.Cashier)
                .Include(o => o.Table)
                .Include(o => o.Branch)
                .Where(o => o.CreatedAt.Date == day);
            if (branchId.HasValue) ordersQuery = ordersQuery.Where(o => o.BranchId == branchId);

            var allOrders = await ordersQuery.OrderBy(o => o.CreatedAt).ToListAsync();
            var soldOrders = allOrders.Where(o => completedStatuses.Contains(o.Status)).ToList();
            var cancelledOrders = allOrders.Where(o => o.Status == OrderStatus.Cancelled).ToList();

            var refundsQuery = _context.Refunds
                .Include(r => r.OriginalOrder)
                .Include(r => r.ProcessedBy)
                .Include(r => r.Items)
                .Where(r => r.CreatedAt.Date == day && r.Status == RefundStatus.Completed);
            if (branchId.HasValue) refundsQuery = refundsQuery.Where(r => r.BranchId == branchId);
            var refunds = await refundsQuery.OrderBy(r => r.CreatedAt).ToListAsync();

            var grossSales = soldOrders.Sum(o => o.Total);
            var refundedAmount = refunds.Sum(r => r.RefundTotal);

            var itemLines = soldOrders.SelectMany(o => o.Items).ToList();
            var soldItems = itemLines
                .GroupBy(i => !string.IsNullOrEmpty(i.ProductNameAr) ? i.ProductNameAr : (i.ProductName ?? "—"))
                .Select(g => new SoldItemRow { ProductName = g.Key, Quantity = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.TotalPrice) })
                .OrderByDescending(x => x.Revenue)
                .ToList();

            var paymentBreakdown = soldOrders.GroupBy(o => o.PaymentMethod.ToString())
                .Select(g => new PaymentBreakdownRow { Method = g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) })
                .OrderByDescending(x => x.Total)
                .ToList();

            // Amortized cost of goods: any distribution whose "expected to last N days" window covers this day
            var distributionsQuery = _context.InventoryLogs
                .Include(l => l.Product)
                .Include(l => l.Branch)
                .Where(l => l.Type == InventoryLogType.Distribution
                         && l.ExpectedDurationDays != null && l.ExpectedDurationDays > 0
                         && l.CreatedAt.Date <= day);
            if (branchId.HasValue) distributionsQuery = distributionsQuery.Where(l => l.BranchId == branchId);
            var candidateDistributions = await distributionsQuery.ToListAsync();

            var costBreakdown = new List<DailyCostBreakdownRow>();
            foreach (var l in candidateDistributions)
            {
                var days = l.ExpectedDurationDays!.Value;
                var windowEnd = l.CreatedAt.Date.AddDays(days - 1);
                if (day < l.CreatedAt.Date || day > windowEnd) continue; // outside its "will last N days" window

                var qty = -l.QuantityChange;
                var dailyPortion = (l.UnitCostAtEvent ?? 0) * qty / days;
                costBreakdown.Add(new DailyCostBreakdownRow
                {
                    Product = !string.IsNullOrEmpty(l.Product?.NameAr) ? l.Product!.NameAr : (l.Product?.Name ?? "—"),
                    Branch = l.Branch?.NameAr ?? "—",
                    DistributedQuantity = qty,
                    ExpectedDurationDays = days,
                    DayNumberInBatch = (day - l.CreatedAt.Date).Days + 1,
                    DailyPortion = dailyPortion
                });
            }
            var estimatedCogs = costBreakdown.Sum(x => x.DailyPortion);

            var model = new DailyReportViewModel
            {
                Date = day,
                DayNameAr = GetArabicDayName(day),
                GrossSales = grossSales,
                CollectedSales = soldOrders.Where(o => o.IsPaid).Sum(o => o.Total),
                OutstandingSales = soldOrders.Where(o => !o.IsPaid).Sum(o => o.Total),
                OpenTabsCount = soldOrders.Count(o => !o.IsPaid),
                WarehousePurchases = await _context.SupplyReceipts
                    .Where(r => r.Date.Date == day)
                    .SumAsync(r => (decimal?)r.TotalCost) ?? 0,
                RefundedAmount = refundedAmount,
                NetSales = grossSales - refundedAmount,
                OrdersCount = soldOrders.Count,
                CancelledCount = cancelledOrders.Count,
                AvgOrder = soldOrders.Count > 0 ? grossSales / soldOrders.Count : 0,
                EstimatedCogs = estimatedCogs,
                EstimatedProfit = grossSales - refundedAmount - estimatedCogs,
                CostBreakdown = costBreakdown.OrderBy(x => x.Branch).ThenBy(x => x.Product).ToList(),
                PaymentBreakdown = paymentBreakdown,
                SoldItems = soldItems,
                SoldOrders = soldOrders,
                CancelledOrders = cancelledOrders,
                Refunds = refunds
            };

            return View(model);
        }

        // ═══════════════════════════════════════════════════
        // REVENUE & PROFIT ANALYSIS — full breakdown of where
        // every pound of revenue came from and where it went
        // ═══════════════════════════════════════════════════
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> RevenueAnalysis(DateTime? from, DateTime? to, int? branchId)
        {
            var start = (from ?? DateTime.Today.AddDays(-29)).Date;
            var end = (to ?? DateTime.Today).Date;
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.BranchId = branchId;

            var sold = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };

            var ordersQ = _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Category)
                .Include(o => o.Cashier)
                .Include(o => o.Branch)
                .Include(o => o.Table)
                .Where(o => o.CreatedAt.Date >= start && o.CreatedAt.Date <= end && sold.Contains(o.Status));
            if (branchId.HasValue) ordersQ = ordersQ.Where(o => o.BranchId == branchId);
            var orders = await ordersQ.ToListAsync();

            var refundsQ = _context.Refunds
                .Include(r => r.Items)
                .Where(r => r.CreatedAt.Date >= start && r.CreatedAt.Date <= end && r.Status == RefundStatus.Completed);
            if (branchId.HasValue) refundsQ = refundsQ.Where(r => r.BranchId == branchId);
            var refunds = await refundsQ.ToListAsync();

            var expensesQ = _context.Expenses.Where(e => e.Date.Date >= start && e.Date.Date <= end);
            if (branchId.HasValue) expensesQ = expensesQ.Where(e => e.BranchId == branchId);
            var expenses = await expensesQ.ToListAsync();

            var wasteQ = _context.InventoryLogs
                .Where(l => l.Type == InventoryLogType.Waste && l.CreatedAt.Date >= start && l.CreatedAt.Date <= end);
            if (branchId.HasValue) wasteQ = wasteQ.Where(l => l.BranchId == branchId);
            var wasteValue = await wasteQ.SumAsync(l => (decimal?)l.MovementValue) ?? 0;

            var purchases = await _context.SupplyReceipts
                .Where(r => r.Date.Date >= start && r.Date.Date <= end)
                .SumAsync(r => (decimal?)r.TotalCost) ?? 0;

            var lines = orders.SelectMany(o => o.Items.Select(i => new { o, i })).ToList();

            // Fall back to the product's current cost for rows saved before cost snapshots existed
            decimal LineCost(OrderItem i) => i.TotalCost > 0
                ? i.TotalCost
                : (i.Product?.CostPrice ?? 0) * i.Quantity;

            var grossSales = orders.Sum(o => o.SubTotal);
            var discounts = orders.Sum(o => o.DiscountAmount);
            var refunded = refunds.Sum(r => r.RefundTotal);
            var netRevenue = orders.Sum(o => o.Total) - refunded;
            var cogs = lines.Sum(x => LineCost(x.i));
            var grossProfit = netRevenue - cogs;
            var opex = expenses.Sum(e => e.Amount);
            var netProfit = grossProfit - opex - wasteValue;

            string PName(OrderItem i) => !string.IsNullOrEmpty(i.ProductNameAr) ? i.ProductNameAr
                                        : (!string.IsNullOrEmpty(i.ProductName) ? i.ProductName : "—");

            var model = new RevenueAnalysisViewModel
            {
                From = start,
                To = end,
                GrossSales = grossSales,
                Discounts = discounts,
                Refunded = refunded,
                NetRevenue = netRevenue,
                Cogs = cogs,
                GrossProfit = grossProfit,
                OperatingExpenses = opex,
                WasteValue = wasteValue,
                NetProfit = netProfit,
                WarehousePurchases = purchases,
                OrderCount = orders.Count,
                ItemsSold = lines.Sum(x => x.i.Quantity),
                Collected = orders.Where(o => o.IsPaid).Sum(o => o.Total),
                Outstanding = orders.Where(o => !o.IsPaid).Sum(o => o.Total),

                ByProduct = lines.GroupBy(x => PName(x.i)).Select(g => new ProfitRow
                {
                    Label = g.Key,
                    Quantity = g.Sum(x => x.i.Quantity),
                    Revenue = g.Sum(x => x.i.TotalPrice),
                    Cost = g.Sum(x => LineCost(x.i))
                }).OrderByDescending(r => r.Revenue).ToList(),

                ByCategory = lines.GroupBy(x => x.i.Product?.Category?.NameAr ?? "بدون فئة").Select(g => new ProfitRow
                {
                    Label = g.Key,
                    Quantity = g.Sum(x => x.i.Quantity),
                    Revenue = g.Sum(x => x.i.TotalPrice),
                    Cost = g.Sum(x => LineCost(x.i))
                }).OrderByDescending(r => r.Revenue).ToList(),

                ByOrderType = orders.GroupBy(o => o.OrderType.ToString()).Select(g => new ProfitRow
                {
                    Label = g.Key,
                    Quantity = g.Count(),
                    Revenue = g.Sum(o => o.Total),
                    Cost = g.SelectMany(o => o.Items).Sum(LineCost)
                }).OrderByDescending(r => r.Revenue).ToList(),

                ByBranch = orders.GroupBy(o => o.Branch?.NameAr ?? "—").Select(g => new ProfitRow
                {
                    Label = g.Key,
                    Quantity = g.Count(),
                    Revenue = g.Sum(o => o.Total),
                    Cost = g.SelectMany(o => o.Items).Sum(LineCost)
                }).OrderByDescending(r => r.Revenue).ToList(),

                ByCashier = orders.GroupBy(o => o.Cashier != null
                        ? (string.IsNullOrEmpty(o.Cashier.FullNameAr) ? o.Cashier.UserName! : o.Cashier.FullNameAr)
                        : "—").Select(g => new ProfitRow
                        {
                            Label = g.Key,
                            Quantity = g.Count(),
                            Revenue = g.Sum(o => o.Total),
                            Cost = g.SelectMany(o => o.Items).Sum(LineCost)
                        }).OrderByDescending(r => r.Revenue).ToList(),

                ByPaymentMethod = orders.GroupBy(o => o.PaymentMethod.ToString()).Select(g => new ProfitRow
                {
                    Label = g.Key,
                    Quantity = g.Count(),
                    Revenue = g.Sum(o => o.Total),
                    Cost = 0
                }).OrderByDescending(r => r.Revenue).ToList(),

                ByHour = orders.GroupBy(o => o.CreatedAt.Hour).Select(g => new ProfitRow
                {
                    Label = $"{g.Key:D2}:00",
                    Quantity = g.Count(),
                    Revenue = g.Sum(o => o.Total),
                    Cost = g.SelectMany(o => o.Items).Sum(LineCost)
                }).OrderBy(r => r.Label).ToList(),

                ByDay = orders.GroupBy(o => o.CreatedAt.Date).Select(g => new ProfitRow
                {
                    Label = g.Key.ToString("yyyy-MM-dd"),
                    Quantity = g.Count(),
                    Revenue = g.Sum(o => o.Total),
                    Cost = g.SelectMany(o => o.Items).Sum(LineCost)
                }).OrderBy(r => r.Label).ToList(),

                ByExpenseCategory = expenses.GroupBy(e => string.IsNullOrEmpty(e.Category) ? "غير مصنف" : e.Category)
                    .Select(g => new ProfitRow
                    {
                        Label = g.Key,
                        Quantity = g.Count(),
                        Revenue = g.Sum(e => e.Amount),
                        Cost = 0
                    }).OrderByDescending(r => r.Revenue).ToList()
            };

            return View(model);
        }

        // ═══════════════════════════════════════════════════
        // SETTINGS
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Settings()
        {
            var settings = await _context.SystemSettings.ToListAsync();
            ViewBag.Branches = await _context.Branches.OrderBy(b => b.Name).ToListAsync();
            var dict = settings
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.Last().Value);
            return View(dict);
        }

        [HttpPost]
        public async Task<IActionResult> SaveSettings([FromBody] Dictionary<string, string> settings)
        {
            if (settings == null) return Json(new { success = false });
            foreach (var kvp in settings)
            {
                var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == kvp.Key);
                if (setting != null) setting.Value = kvp.Value;
                else _context.SystemSettings.Add(new SystemSettings { Key = kvp.Key, Value = kvp.Value });
            }
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ═══════════════════════════════════════════════════
        // SHIFTS
        // ═══════════════════════════════════════════════════
        public async Task<IActionResult> Shifts()
        {
            var shifts = await _context.Shifts
                .Include(s => s.User).Include(s => s.Branch)
                .OrderByDescending(s => s.StartTime)
                .Take(100).ToListAsync();

            // بيانات المحل لطباعة تقرير الوردية (لوجو/اسم/هاتف مطبوعين في الإيصال)
            var settings = await _context.SystemSettings.Where(s => s.BranchId == null).ToListAsync();
            ViewBag.Settings = settings.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Value);

            return View(shifts);
        }

        // ── تقرير الوردية الكامل — قبل التصفير عايز تشوف/تطبع كل حاجة الكاشير عملها ──
        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetShiftReport(int shiftId)
        {
            var shift = await _context.Shifts
                .Include(s => s.User).Include(s => s.Branch)
                .FirstOrDefaultAsync(s => s.Id == shiftId);
            if (shift == null) return Json(new { success = false, message = "الوردية غير موجودة" });

            // نفس منطق CloseShift بالظبط، بس لأي وردية يحددها الأدمن مش بس وردية المستخدم الحالي
            var end = shift.EndTime ?? DateTime.Now;
            var completed = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };

            var orders = await _context.Orders
                .Include(o => o.Items)
                .Where(o => o.CashierId == shift.UserId && o.BranchId == shift.BranchId
                         && o.CreatedAt >= shift.StartTime && o.CreatedAt <= end)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            var soldOrders = orders.Where(o => completed.Contains(o.Status)).ToList();
            var cancelledOrders = orders.Where(o => o.Status == OrderStatus.Cancelled).ToList();

            var refunds = await _context.Refunds
                .Include(r => r.OriginalOrder)
                .Where(r => r.ProcessedById == shift.UserId && r.BranchId == shift.BranchId
                         && r.CreatedAt >= shift.StartTime && r.CreatedAt <= end
                         && r.Status == RefundStatus.Completed)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            var grossSales = soldOrders.Sum(o => o.Total);
            var refundTotal = refunds.Sum(r => r.RefundTotal);
            var netSales = Math.Max(0, grossSales - refundTotal);

            var cashSales = soldOrders.Where(o => o.PaymentMethod == PaymentMethod.Cash).Sum(o => o.Total);
            var cashRefunds = refunds.Where(r => r.RefundMethod == RefundMethod.Cash).Sum(r => r.RefundTotal);
            var expectedCash = shift.OpeningCash + Math.Max(0, cashSales - cashRefunds);

            var itemsSoldCount = soldOrders.SelectMany(o => o.Items).Sum(i => i.Quantity);
            var topProducts = soldOrders.SelectMany(o => o.Items)
                .GroupBy(i => !string.IsNullOrEmpty(i.ProductNameAr) ? i.ProductNameAr : (i.ProductName ?? "—"))
                .Select(g => new { productName = g.Key, quantity = g.Sum(i => i.Quantity), total = g.Sum(i => i.TotalPrice) })
                .OrderByDescending(x => x.total)
                .ToList();

            return Json(new
            {
                success = true,
                shiftId = shift.Id,
                cashier = string.IsNullOrEmpty(shift.User?.FullNameAr) ? shift.User?.UserName : shift.User!.FullNameAr,
                branch = shift.Branch?.NameAr ?? "—",
                startTime = shift.StartTime,
                endTime = shift.IsClosed ? shift.EndTime : (DateTime?)null,   // لسه شغالة لو مقفولاش
                isClosed = shift.IsClosed,
                openingCash = shift.OpeningCash,
                closingCash = shift.IsClosed ? shift.ClosingCash : (decimal?)null,

                ordersCount = soldOrders.Count,
                cancelledCount = cancelledOrders.Count,
                itemsSoldCount,
                grossSales,
                refundTotal,
                netSales,
                expectedCash,

                paymentBreakdown = soldOrders.GroupBy(o => o.PaymentMethod.ToString())
                    .Select(g => new { method = g.Key, count = g.Count(), total = g.Sum(o => o.Total) }),

                topProducts,

                orders = soldOrders.Select(o => new
                {
                    o.OrderNumber,
                    time = o.CreatedAt.ToString("HH:mm"),
                    type = o.OrderType.ToString(),
                    payment = o.PaymentMethod.ToString(),
                    total = o.Total,
                    status = o.Status.ToString()
                }),

                refunds = refunds.Select(r => new
                {
                    r.RefundNumber,
                    time = r.CreatedAt.ToString("HH:mm"),
                    originalOrder = r.OriginalOrder != null ? r.OriginalOrder.OrderNumber : "—",
                    amount = r.RefundTotal,
                    reason = r.Reason ?? "—"
                }),

                hasRefunds = refunds.Any()
            });
        }

        // ── تصفير الوردية — الأدمن بيقفلها بدل الكاشير (بعد ما يطبع التقرير) ──
        [HttpPost]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> ForceCloseShift([FromBody] ForceCloseShiftRequest req)
        {
            var shift = await _context.Shifts.FirstOrDefaultAsync(s => s.Id == req.ShiftId);
            if (shift == null) return Json(new { success = false, message = "الوردية غير موجودة" });
            if (shift.IsClosed) return Json(new { success = false, message = "الوردية مقفولة بالفعل" });

            var completed = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };
            var gross = await _context.Orders
                .Where(o => o.CashierId == shift.UserId && o.BranchId == shift.BranchId
                         && o.CreatedAt >= shift.StartTime && completed.Contains(o.Status)
                         && o.PaymentMethod == PaymentMethod.Cash)
                .SumAsync(o => o.Total);
            var refunds = await _context.Refunds
                .Where(r => r.ProcessedById == shift.UserId && r.BranchId == shift.BranchId
                         && r.CreatedAt >= shift.StartTime && r.Status == RefundStatus.Completed
                         && r.RefundMethod == RefundMethod.Cash)
                .SumAsync(r => r.RefundTotal);

            shift.EndTime = DateTime.Now;
            shift.ClosingCash = req.ClosingCash;
            shift.TotalSales = Math.Max(0, gross - refunds);
            shift.IsClosed = true;
            shift.Notes = string.IsNullOrWhiteSpace(req.Notes)
                ? $"تم التصفير بواسطة {_userManager.GetUserName(User)}"
                : $"{req.Notes} — تم التصفير بواسطة {_userManager.GetUserName(User)}";
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                totalSales = shift.TotalSales,
                expectedCash = shift.OpeningCash + shift.TotalSales,
                difference = req.ClosingCash - (shift.OpeningCash + shift.TotalSales)
            });
        }

        // ═══════════════════════════════════════════════════
        // REPORTS
        // ═══════════════════════════════════════════════════
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> Reports()
        {
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            return View();
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetReportData(int days = 30, int? branchId = null)
        {
            var from = DateTime.Today.AddDays(-days);
            var previousFrom = from.AddDays(-days);
            var completedStatuses = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };

            IQueryable<Order> OQ() { var q = _context.Orders.AsQueryable(); if (branchId.HasValue) q = q.Where(o => o.BranchId == branchId); return q; }
            IQueryable<Refund> RQ() { var q = _context.Refunds.AsQueryable(); if (branchId.HasValue) q = q.Where(r => r.BranchId == branchId); return q; }
            IQueryable<Expense> EQ() { var q = _context.Expenses.AsQueryable(); if (branchId.HasValue) q = q.Where(e => e.BranchId == branchId); return q; }
            IQueryable<OrderItem> OIQ() { var q = _context.OrderItems.AsQueryable(); if (branchId.HasValue) q = q.Where(oi => oi.Order!.BranchId == branchId); return q; }

            var completedOrders = await OQ().Where(o => completedStatuses.Contains(o.Status) && o.CreatedAt.Date >= from).ToListAsync();
            var previousOrders = await OQ().Where(o => completedStatuses.Contains(o.Status) && o.CreatedAt.Date >= previousFrom && o.CreatedAt.Date < from).ToListAsync();
            var periodRefunds = await RQ().Where(r => r.CreatedAt.Date >= from && r.Status == RefundStatus.Completed).ToListAsync();
            var prevPeriodRefunds = await RQ().Where(r => r.CreatedAt.Date >= previousFrom && r.CreatedAt.Date < from && r.Status == RefundStatus.Completed).ToListAsync();
            var periodExpenses = await EQ().Where(e => e.Date >= from).ToListAsync();

            var expenseByDay = periodExpenses.GroupBy(e => e.Date.Date).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
            var totalExpenses = periodExpenses.Sum(e => e.Amount);
            var grossRevenue = completedOrders.Sum(o => o.Total);
            var refundedAmount = periodRefunds.Sum(r => r.RefundTotal);
            var totalRevenue = Math.Max(0, grossRevenue - refundedAmount);
            var prevRevenue = Math.Max(0, previousOrders.Sum(o => o.Total) - prevPeriodRefunds.Sum(r => r.RefundTotal));
            var revenueGrowth = prevRevenue > 0 ? Math.Round((totalRevenue - prevRevenue) / prevRevenue * 100, 1) : 0m;
            var ordersGrowth = previousOrders.Count > 0 ? Math.Round((decimal)(completedOrders.Count - previousOrders.Count) / previousOrders.Count * 100, 1) : 0m;

            var totalCogs = await OIQ()
                .Include(oi => oi.Product)
                .Where(oi => completedStatuses.Contains(oi.Order!.Status) && oi.Order.CreatedAt.Date >= from && oi.Product != null)
                .SumAsync(oi => oi.Product!.CostPrice * oi.Quantity);

            var grossProfit = totalRevenue - totalCogs;
            var netProfit = grossProfit - totalExpenses;
            var profitMargin = totalRevenue > 0 ? Math.Round(netProfit / totalRevenue * 100, 1) : 0m;

            var refundByDay = periodRefunds.GroupBy(r => r.CreatedAt.Date).ToDictionary(g => g.Key, g => g.Sum(r => r.RefundTotal));
            var allDates = Enumerable.Range(0, days).Select(i => from.AddDays(i)).ToList();
            var ordersByDay = completedOrders.GroupBy(o => o.CreatedAt.Date).ToDictionary(g => g.Key, g => g.ToList());

            var dailySales = allDates.Select(date =>
            {
                var ords = ordersByDay.GetValueOrDefault(date, new());
                var gross = ords.Sum(o => o.Total);
                var refunds = refundByDay.GetValueOrDefault(date, 0);
                var expenses = expenseByDay.GetValueOrDefault(date, 0);
                var rev = Math.Max(0, gross - refunds);
                return new { date = date.ToString("MM/dd"), fullDate = date.ToString("yyyy-MM-dd"), dayName = date.ToString("ddd"), dayNameAr = GetArabicDayName(date), revenue = rev, orders = ords.Count, refunds, expenses, profit = rev - expenses };
            }).ToList();

            var weeklySales = allDates
                .GroupBy(d => System.Globalization.CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Monday))
                .Select(wg => { var wDates = wg.ToList(); var wOrds = wDates.SelectMany(d => ordersByDay.GetValueOrDefault(d, new())).ToList(); var wGross = wOrds.Sum(o => o.Total); var wRef = wDates.Sum(d => refundByDay.GetValueOrDefault(d, 0)); var wExp = wDates.Sum(d => expenseByDay.GetValueOrDefault(d, 0)); var wRev = Math.Max(0, wGross - wRef); return new { week = $"W{wg.Key} ({wDates.First():MM/dd}–{wDates.Last():MM/dd})", revenue = wRev, orders = wOrds.Count, expenses = wExp, profit = wRev - wExp }; }).ToList();

            var monthlySales = allDates
                .GroupBy(d => new { d.Year, d.Month })
                .Select(mg => { var mDates = mg.ToList(); var mOrds = mDates.SelectMany(d => ordersByDay.GetValueOrDefault(d, new())).ToList(); var mGross = mOrds.Sum(o => o.Total); var mRef = mDates.Sum(d => refundByDay.GetValueOrDefault(d, 0)); var mExp = mDates.Sum(d => expenseByDay.GetValueOrDefault(d, 0)); var mRev = Math.Max(0, mGross - mRef); return new { month = new DateTime(mg.Key.Year, mg.Key.Month, 1).ToString("MMMM yyyy"), revenue = mRev, orders = mOrds.Count, expenses = mExp, profit = mRev - mExp }; }).ToList();

            var topProducts = await OIQ()
                .Include(oi => oi.Product)
                .Where(oi => completedStatuses.Contains(oi.Order!.Status) && oi.Order.CreatedAt.Date >= from)
                .GroupBy(oi => new { oi.ProductId, oi.ProductName, oi.ProductNameAr })
                .Select(g => new { productId = g.Key.ProductId, productName = g.Key.ProductName, productNameAr = g.Key.ProductNameAr, totalQty = g.Sum(oi => oi.Quantity), totalRevenue = g.Sum(oi => oi.TotalPrice) })
                .OrderByDescending(x => x.totalRevenue).Take(15).ToListAsync();

            var topWithCost = new List<object>();
            foreach (var p in topProducts)
            {
                var product = await _context.Products.FindAsync(p.productId);
                var cost = (product?.CostPrice ?? 0) * p.totalQty;
                var margin = p.totalRevenue > 0 ? Math.Round((p.totalRevenue - cost) / p.totalRevenue * 100, 1) : 0;
                topWithCost.Add(new { p.productName, p.productNameAr, p.totalQty, p.totalRevenue, cost, profit = p.totalRevenue - cost, margin });
            }

            var categoryRevenue = await OIQ()
                .Include(oi => oi.Product).ThenInclude(p => p!.Category)
                .Where(oi => completedStatuses.Contains(oi.Order!.Status) && oi.Order.CreatedAt.Date >= from && oi.Product!.Category != null)
                .GroupBy(oi => new { oi.Product!.Category!.Name, oi.Product.Category.NameAr, oi.Product.Category.ColorHex })
                .Select(g => new { categoryName = g.Key.Name, categoryNameAr = g.Key.NameAr, colorHex = g.Key.ColorHex, revenue = g.Sum(oi => oi.TotalPrice), qty = g.Sum(oi => oi.Quantity) })
                .OrderByDescending(x => x.revenue).ToListAsync();

            var paymentMethods = completedOrders.GroupBy(o => o.PaymentMethod.ToString()).ToDictionary(g => g.Key, g => new { count = g.Count(), revenue = g.Sum(o => o.Total) });
            var orderTypes = completedOrders.GroupBy(o => o.OrderType.ToString()).ToDictionary(g => g.Key, g => new { count = g.Count(), revenue = g.Sum(o => o.Total) });

            List<object> branchBreakdown = new();
            if (!branchId.HasValue)
            {
                var branches = await _context.Branches.Where(b => b.IsActive).ToListAsync();
                foreach (var b in branches)
                {
                    var bRev = await _context.Orders.Where(o => o.BranchId == b.Id && completedStatuses.Contains(o.Status) && o.CreatedAt.Date >= from).SumAsync(o => o.Total);
                    var bRef = await _context.Refunds.Where(r => r.BranchId == b.Id && r.Status == RefundStatus.Completed && r.CreatedAt.Date >= from).SumAsync(r => r.RefundTotal);
                    var bExp = await _context.Expenses.Where(e => e.BranchId == b.Id && e.Date >= from).SumAsync(e => e.Amount);
                    var bNetRev = Math.Max(0, bRev - bRef);
                    var bOrders = await _context.Orders.CountAsync(o => o.BranchId == b.Id && completedStatuses.Contains(o.Status) && o.CreatedAt.Date >= from);
                    branchBreakdown.Add(new { branchId = b.Id, branchName = b.Name, branchNameAr = b.NameAr, icon = b.Icon, colorHex = b.ColorHex, revenue = bNetRev, expenses = bExp, profit = bNetRev - bExp, orders = bOrders });
                }
            }

            return Json(new
            {
                totalRevenue,
                totalOrders = completedOrders.Count,
                avgOrderValue = completedOrders.Count > 0 ? totalRevenue / completedOrders.Count : 0,
                revenueGrowth,
                ordersGrowth,
                uniqueCustomers = completedOrders.Where(o => !string.IsNullOrEmpty(o.CustomerName)).Select(o => o.CustomerName).Distinct().Count(),
                totalExpenses,
                totalCogs,
                grossProfit,
                netProfit,
                profitMargin,
                totalRefunded = refundedAmount,
                totalRefundCount = periodRefunds.Count,
                periodDays = days,
                dailySales,
                weeklySales,
                monthlySales,
                topProducts = topWithCost,
                categoryRevenue,
                paymentMethods,
                orderTypes,
                branchBreakdown,
                generatedAt = DateTime.Now
            });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetDashboardStats()
        {
            var stats = await _analytics.GetDashboardStatsAsync();
            return Json(stats);
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetRecentOrders(int count = 10)
        {
            var orders = await _context.Orders
                .Include(o => o.Cashier).Include(o => o.Items)
                .Where(o => o.CreatedAt.Date == DateTime.Today)
                .OrderByDescending(o => o.CreatedAt).Take(count)
                .Select(o => new { o.Id, o.OrderNumber, o.OrderType, o.Status, o.Total, o.PaymentMethod, cashierName = o.Cashier != null ? o.Cashier.UserName : null, itemCount = o.Items != null ? o.Items.Count : 0, createdAt = o.CreatedAt })
                .ToListAsync();
            return Json(new { orders });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetActiveShifts()
        {
            var shifts = await _context.Shifts.Include(s => s.User)
                .Where(s => !s.IsClosed)
                .Select(s => new { s.Id, s.TotalSales, cashierName = s.User != null ? s.User.UserName : null, startTime = s.StartTime.ToString("hh:mm tt") })
                .ToListAsync();
            return Json(new { shifts });
        }

        // ═══════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════
        private static string GetArabicDayName(DateTime date) => date.DayOfWeek switch
        {
            DayOfWeek.Saturday => "السبت",
            DayOfWeek.Sunday => "الأحد",
            DayOfWeek.Monday => "الاثنين",
            DayOfWeek.Tuesday => "الثلاثاء",
            DayOfWeek.Wednesday => "الأربعاء",
            DayOfWeek.Thursday => "الخميس",
            DayOfWeek.Friday => "الجمعة",
            _ => ""
        };

        // ═══════════════════════════════════════════════════
        // DTOs / REQUEST MODELS
        // ═══════════════════════════════════════════════════
        public class SaveExpenseDto
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string? Description { get; set; }
            public decimal Amount { get; set; }
            public string Category { get; set; } = "";
            public string? PaymentMethod { get; set; }
            public string? Notes { get; set; }
            public DateTime Date { get; set; } = DateTime.Now;
            public int? BranchId { get; set; }
        }
        public class CreateUserRequest { public string Email { get; set; } = ""; public string FullName { get; set; } = ""; public string FullNameAr { get; set; } = ""; public string Password { get; set; } = ""; public string Role { get; set; } = ""; public List<int> BranchIds { get; set; } = new(); public int? PrimaryBranchId { get; set; } }
        public class EditUserRequest { public string UserId { get; set; } = ""; public string FullName { get; set; } = ""; public string FullNameAr { get; set; } = ""; public string Email { get; set; } = ""; public string Role { get; set; } = ""; }
        public class ToggleUserRequest { public string UserId { get; set; } = ""; }
        public class ResetPasswordRequest { public string UserId { get; set; } = ""; public string NewPassword { get; set; } = ""; }
        public class UpdateUserBranchesRequest { public string UserId { get; set; } = ""; public List<int> BranchIds { get; set; } = new(); public int? PrimaryBranchId { get; set; } }
        public class UpdateTableStatusRequest { public int TableId { get; set; } public string Status { get; set; } = ""; }
        public class UpdateStockRequest { public int ProductId { get; set; } public int Quantity { get; set; } public string Reason { get; set; } = ""; }
        public class DeleteByIdRequest { public int Id { get; set; } }
        public class DeleteByIdStringRequest { public string Id { get; set; } = ""; }
        public class ForceCloseShiftRequest { public int ShiftId { get; set; } public decimal ClosingCash { get; set; } public string? Notes { get; set; } }
    }

    public class DailyReportViewModel
    {
        public DateTime Date { get; set; }
        public string DayNameAr { get; set; } = "";
        public decimal GrossSales { get; set; }
        public decimal RefundedAmount { get; set; }
        public decimal NetSales { get; set; }
        public decimal CollectedSales { get; set; }
        public decimal OutstandingSales { get; set; }
        public int OpenTabsCount { get; set; }
        public decimal WarehousePurchases { get; set; }
        public int OrdersCount { get; set; }
        public int CancelledCount { get; set; }
        public decimal AvgOrder { get; set; }
        public decimal EstimatedCogs { get; set; }
        public decimal EstimatedProfit { get; set; }
        public List<DailyCostBreakdownRow> CostBreakdown { get; set; } = new();
        public List<PaymentBreakdownRow> PaymentBreakdown { get; set; } = new();
        public List<SoldItemRow> SoldItems { get; set; } = new();
        public List<Order> SoldOrders { get; set; } = new();
        public List<Order> CancelledOrders { get; set; } = new();
        public List<Refund> Refunds { get; set; } = new();
    }

    public class DailyCostBreakdownRow
    {
        public string Product { get; set; } = "";
        public string Branch { get; set; } = "";
        public decimal DistributedQuantity { get; set; }
        public int ExpectedDurationDays { get; set; }
        public int DayNumberInBatch { get; set; }
        public decimal DailyPortion { get; set; }
    }

    public class PaymentBreakdownRow { public string Method { get; set; } = ""; public int Count { get; set; } public decimal Total { get; set; } }
    public class SoldItemRow { public string ProductName { get; set; } = ""; public int Quantity { get; set; } public decimal Revenue { get; set; } }

    public class RevenueAnalysisViewModel
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public decimal GrossSales { get; set; }
        public decimal Discounts { get; set; }
        public decimal Refunded { get; set; }
        public decimal NetRevenue { get; set; }
        public decimal Cogs { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal OperatingExpenses { get; set; }
        public decimal WasteValue { get; set; }
        public decimal NetProfit { get; set; }
        public decimal WarehousePurchases { get; set; }
        public decimal Collected { get; set; }
        public decimal Outstanding { get; set; }
        public int OrderCount { get; set; }
        public int ItemsSold { get; set; }

        public decimal GrossMargin => NetRevenue > 0 ? GrossProfit / NetRevenue * 100 : 0;
        public decimal NetMargin => NetRevenue > 0 ? NetProfit / NetRevenue * 100 : 0;
        public decimal AvgOrderValue => OrderCount > 0 ? NetRevenue / OrderCount : 0;

        public List<ProfitRow> ByProduct { get; set; } = new();
        public List<ProfitRow> ByCategory { get; set; } = new();
        public List<ProfitRow> ByOrderType { get; set; } = new();
        public List<ProfitRow> ByBranch { get; set; } = new();
        public List<ProfitRow> ByCashier { get; set; } = new();
        public List<ProfitRow> ByPaymentMethod { get; set; } = new();
        public List<ProfitRow> ByHour { get; set; } = new();
        public List<ProfitRow> ByDay { get; set; } = new();
        public List<ProfitRow> ByExpenseCategory { get; set; } = new();
    }

    public class ProfitRow
    {
        public string Label { get; set; } = "";
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public decimal Cost { get; set; }
        public decimal Profit => Revenue - Cost;
        public decimal Margin => Revenue > 0 ? Profit / Revenue * 100 : 0;
    }

    public class BreakdownVm
    {
        public string Title { get; set; } = "";
        public string ColLabel { get; set; } = "";
        public string QtyLabel { get; set; } = "";
        public bool ShowProfit { get; set; }
        public decimal Total { get; set; }
        public List<ProfitRow> Rows { get; set; } = new();
    }
}
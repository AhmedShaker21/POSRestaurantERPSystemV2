using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    // ═══════════════════════════════════════════════════════════════
    //  WAREHOUSE MODULE
    //  One central warehouse feeding N branches. Everything is stored in
    //  the product's base unit; packaging (كرتونة/شوال) is an input helper.
    // ═══════════════════════════════════════════════════════════════
    [Authorize(Roles = "Admin,Manager")]
    public class InventoryController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly InventoryService _inventory;

        public InventoryController(ApplicationDbContext context,
                                   UserManager<ApplicationUser> userManager,
                                   InventoryService inventory)
        {
            _context = context;
            _userManager = userManager;
            _inventory = inventory;
        }

        private bool IsAdmin => User.IsInRole("Admin");

        // ═══════════════════ DASHBOARD ═══════════════════
        public async Task<IActionResult> Dashboard()
        {
            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();

            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches).ThenInclude(pb => pb.Branch)
                .Where(p => p.IsActive)
                .OrderBy(p => p.NameAr)
                .ToListAsync();

            var today = DateTime.Today;
            var monthStart = new DateTime(today.Year, today.Month, 1);

            var centralValue = products.Sum(p => p.StockQuantity * p.CostPrice);
            var branchValue = products.Sum(p => (p.ProductBranches?.Sum(pb => pb.StockQuantity) ?? 0) * p.CostPrice);

            ViewBag.Branches = branches;
            ViewBag.CentralValue = centralValue;
            ViewBag.BranchValue = branchValue;
            ViewBag.TotalValue = centralValue + branchValue;

            ViewBag.MonthPurchases = await _context.SupplyReceipts
                .Where(r => r.Date >= monthStart)
                .SumAsync(r => (decimal?)r.TotalCost) ?? 0;

            ViewBag.Outstanding = await _context.SupplyReceipts
                .Where(r => !r.IsPaid)
                .SumAsync(r => (decimal?)(r.TotalCost - r.AmountPaid)) ?? 0;

            ViewBag.LowStock = products.Count(p => p.TrackStock && p.StockQuantity <= p.MinStockAlert);

            var (expiredCount, expiringSoon) = await _inventory.GetExpiryAlertCountsAsync();
            ViewBag.ExpiredCount = expiredCount;
            ViewBag.ExpiringSoon = expiringSoon;

            // ── Recent movement, split so the admin can see at a glance what
            //    left the warehouse and what came into it ────────────────────
            var since = today.AddDays(-30);

            ViewBag.RecentOut = await _context.InventoryLogs
                .Include(l => l.Product).Include(l => l.Branch)
                .Where(l => l.CreatedAt.Date >= since
                         && (l.Type == InventoryLogType.Distribution || l.Type == InventoryLogType.Waste))
                .OrderByDescending(l => l.CreatedAt)
                .Take(50)
                .ToListAsync();

            ViewBag.RecentIn = await _context.InventoryLogs
                .Include(l => l.Product).Include(l => l.Branch)
                .Where(l => l.CreatedAt.Date >= since
                         && (l.Type == InventoryLogType.Supply
                          || l.Type == InventoryLogType.NewProduct
                          || l.Type == InventoryLogType.ReturnToWarehouse))
                .OrderByDescending(l => l.CreatedAt)
                .Take(50)
                .ToListAsync();

            // Stock sitting at branches that is still usable (not yet sold/consumed)
            ViewBag.AtBranches = products
                .SelectMany(p => (p.ProductBranches ?? new List<ProductBranch>())
                    .Where(pb => pb.StockQuantity > 0)
                    .Select(pb => new BranchStockRow
                    {
                        Product = string.IsNullOrWhiteSpace(p.NameAr) ? p.Name : p.NameAr,
                        Unit = !string.IsNullOrWhiteSpace(pb.StockUnitLabel) ? pb.StockUnitLabel
                             : (string.IsNullOrWhiteSpace(p.Unit) ? "قطعة" : p.Unit),
                        Branch = pb.Branch?.NameAr ?? "—",
                        Quantity = pb.StockQuantity,
                        IsSellable = pb.IsSellable,
                        Value = pb.StockQuantity * p.CostPrice,
                        LastDistributedAt = pb.LastDistributedAt
                    }))
                .OrderByDescending(r => r.Value)
                .ToList();
            ViewBag.IsAdmin = IsAdmin;

            return View(products);
        }

        // ═══════════════════ SUPPLIERS ═══════════════════
        public async Task<IActionResult> Suppliers()
        {
            var suppliers = await _context.Suppliers
                .Include(s => s.Receipts)
                .OrderBy(s => s.Name)
                .ToListAsync();
            ViewBag.IsAdmin = IsAdmin;
            return View(suppliers);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SaveSupplier([FromBody] SupplierRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Json(new { success = false, message = "اسم المورد مطلوب" });

            Supplier? supplier;
            if (req.Id > 0)
            {
                supplier = await _context.Suppliers.FindAsync(req.Id);
                if (supplier == null) return Json(new { success = false, message = "المورد غير موجود" });
            }
            else
            {
                supplier = new Supplier();
                _context.Suppliers.Add(supplier);
            }

            supplier.Name = req.Name.Trim();
            supplier.Phone = req.Phone;
            supplier.Address = req.Address;
            supplier.Notes = req.Notes;
            supplier.IsActive = req.IsActive;

            await _context.SaveChangesAsync();
            return Json(new { success = true, id = supplier.Id });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteSupplier([FromBody] IdRequest req)
        {
            var supplier = await _context.Suppliers.FindAsync(req.Id);
            if (supplier == null) return Json(new { success = false, message = "المورد غير موجود" });

            if (await _context.SupplyReceipts.AnyAsync(r => r.SupplierId == supplier.Id))
            {
                // Keep history intact — deactivate instead of deleting
                supplier.IsActive = false;
                await _context.SaveChangesAsync();
                return Json(new { success = true, deactivated = true });
            }

            _context.Suppliers.Remove(supplier);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // ═══════════════════ RECEIPTS (الوارد) ═══════════════════
        public async Task<IActionResult> Receipts(DateTime? from, DateTime? to, int? supplierId, string? paid)
        {
            from ??= DateTime.Today.AddDays(-30);
            to ??= DateTime.Today;

            var q = _context.SupplyReceipts
                .Include(r => r.Supplier)
                .Include(r => r.CreatedBy)
                .Include(r => r.Items)
                .Where(r => r.Date.Date >= from.Value.Date && r.Date.Date <= to.Value.Date);

            if (supplierId.HasValue) q = q.Where(r => r.SupplierId == supplierId);
            if (paid == "unpaid") q = q.Where(r => !r.IsPaid);
            else if (paid == "paid") q = q.Where(r => r.IsPaid);

            var receipts = await q.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).ToListAsync();

            ViewBag.From = from.Value;
            ViewBag.To = to.Value;
            ViewBag.SupplierId = supplierId;
            ViewBag.Paid = paid;
            ViewBag.Suppliers = await _context.Suppliers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            ViewBag.Products = await _context.Products.Where(p => p.IsActive).OrderBy(p => p.NameAr).ToListAsync();
            ViewBag.IsAdmin = IsAdmin;

            return View(receipts);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateReceipt([FromBody] CreateReceiptRequest req)
        {
            if (req.Lines == null || !req.Lines.Any())
                return Json(new { success = false, message = "أضف صنفًا واحدًا على الأقل" });

            try
            {
                var receipt = await _inventory.CreateReceiptAsync(
                    req.Lines.Select(l => new InventoryService.ReceiptLineInput
                    {
                        ProductId = l.ProductId,
                        Quantity = l.Quantity,
                        AsPackage = l.AsPackage,
                        CostPerEnteredUnit = l.UnitCost,
                        Unit = l.Unit,
                        PackagingName = l.PackagingName,
                        UnitsPerPackage = l.UnitsPerPackage,
                        ExpiryDate = l.ExpiryDate,
                        ProductionDate = l.ProductionDate,
                        BatchNumber = l.BatchNumber,
                        ShelfLifeDays = l.ShelfLifeDays,
                        ServingName = l.ServingName,
                        ServingsPerUnit = l.ServingsPerUnit
                    }),
                    req.Date ?? DateTime.Now,
                    req.SupplierId,
                    req.SupplierName,
                    req.InvoiceNumber,
                    req.Notes,
                    req.IsPaid,
                    req.AmountPaid,
                    _userManager.GetUserId(User));

                return Json(new
                {
                    success = true,
                    receiptId = receipt.Id,
                    receiptNumber = receipt.ReceiptNumber,
                    total = receipt.TotalCost
                });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ReceiptDetail(int id)
        {
            var receipt = await _context.SupplyReceipts
                .Include(r => r.Supplier)
                .Include(r => r.CreatedBy)
                .Include(r => r.Items).ThenInclude(i => i.Product)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (receipt == null) return NotFound();
            return View(receipt);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SettleReceipt([FromBody] SettleReceiptRequest req)
        {
            var receipt = await _context.SupplyReceipts.FindAsync(req.ReceiptId);
            if (receipt == null) return Json(new { success = false, message = "الإيصال غير موجود" });

            receipt.AmountPaid = Math.Min(receipt.AmountPaid + req.Amount, receipt.TotalCost);
            if (receipt.AmountPaid >= receipt.TotalCost)
            {
                receipt.IsPaid = true;
                receipt.PaidAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, amountPaid = receipt.AmountPaid, isPaid = receipt.IsPaid });
        }

        // ═══════════════════ DISTRIBUTION SCREEN ═══════════════════
        public async Task<IActionResult> Distribution(int? branchId)
        {
            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches).ThenInclude(pb => pb.Branch)
                .Where(p => p.IsActive)
                .OrderBy(p => p.NameAr)
                .ToListAsync();

            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            ViewBag.SelectedBranch = branchId;
            ViewBag.IsAdmin = IsAdmin;

            ViewBag.Recent = await _context.InventoryLogs
                .Include(l => l.Product)
                .Include(l => l.Branch)
                .Where(l => l.Type == InventoryLogType.Distribution)
                .OrderByDescending(l => l.CreatedAt)
                .Take(30)
                .ToListAsync();

            return View(products);
        }

        // ═══════════════════ DISTRIBUTION (المنصرف) ═══════════════════
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Distribute([FromBody] DistributeRequest req)
        {
            if (req.ExpectedDurationDays.HasValue && req.ExpectedDurationDays <= 0)
                return Json(new { success = false, message = "عدد الأيام يجب أن يكون أكبر من صفر" });

            var result = await _inventory.DistributeAsync(
                req.ProductId, req.BranchId, req.Quantity, req.AsPackage,
                req.ExpectedDurationDays, req.Notes, _userManager.GetUserId(User),
                req.IsSellable,
                new InventoryService.DistributionPricing
                {
                    SellingPrice = req.SellingPrice,
                    TaxRateOverride = req.TaxRateOverride,
                    SkipKitchenOverride = req.SkipKitchenOverride,
                    MinStockAlert = req.MinStockAlert,
                    IsAvailable = req.IsAvailable
                });

            return Json(new
            {
                success = result.Success,
                message = result.Message,
                newCentralStock = result.NewCentralStock,
                newBranchStock = result.NewBranchStock,
                baseQuantity = result.BaseQuantity,
                movementValue = result.MovementValue,
                batchNote = result.BatchNote,
                branchUnitLabel = result.BranchUnitLabel,
                branchQuantityAdded = result.BranchQuantityAdded
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Adjust([FromBody] AdjustRequest req)
        {
            if (!Enum.TryParse<InventoryLogType>(req.Type, out var type))
                type = InventoryLogType.Adjustment;

            var (ok, message) = await _inventory.AdjustAsync(
                req.ProductId, req.Quantity, req.AsPackage, type,
                string.IsNullOrWhiteSpace(req.Reason) ? "تسوية مخزون" : req.Reason,
                req.BranchId, _userManager.GetUserId(User));

            return Json(new { success = ok, message });
        }

        [HttpGet]
        public async Task<IActionResult> GetProductBranches(int productId)
        {
            var product = await _context.Products.FindAsync(productId);
            if (product == null) return Json(new { success = false });

            var rows = await _context.ProductBranches
                .Include(pb => pb.Branch)
                .Where(pb => pb.ProductId == productId)
                .OrderBy(pb => pb.Branch!.Name)
                .Select(pb => new { branchId = pb.BranchId, branchName = pb.Branch!.NameAr, stock = pb.StockQuantity })
                .ToListAsync();

            return Json(new
            {
                success = true,
                unit = product.Unit,
                packaging = product.PackagingName,
                unitsPerPackage = product.UnitsPerPackage,
                rows
            });
        }

        // Exact warehouse picture for one product — a product may have been received
        // across many separate supply receipts, so show the accumulated total and the
        // receipts that make it up.
        [HttpGet]
        public async Task<IActionResult> GetProductStock(int productId)
        {
            var p = await _context.Products
                .Include(x => x.ProductBranches).ThenInclude(pb => pb.Branch)
                .FirstOrDefaultAsync(x => x.Id == productId);
            if (p == null) return Json(new { success = false, message = "الصنف غير موجود" });

            var perPack = p.UnitsPerPackage > 0 ? p.UnitsPerPackage : 1;

            var receipts = await _context.SupplyReceiptItems
                .Include(i => i.SupplyReceipt).ThenInclude(r => r!.Supplier)
                .Where(i => i.ProductId == productId)
                .OrderByDescending(i => i.SupplyReceipt!.Date)
                .Take(15)
                .Select(i => new
                {
                    date = i.SupplyReceipt!.Date,
                    receiptNumber = i.SupplyReceipt.ReceiptNumber,
                    supplier = i.SupplyReceipt.Supplier != null
                        ? i.SupplyReceipt.Supplier.Name
                        : i.SupplyReceipt.SupplierNameSnapshot,
                    baseQty = i.BaseQuantity,
                    entered = i.EnteredQuantity,
                    enteredAs = i.EnteredAsPackaging,
                    costPerBase = i.CostPerBaseUnit,
                    lineTotal = i.LineTotal
                })
                .ToListAsync();

            var totalReceived = await _context.InventoryLogs
                .Where(l => l.ProductId == productId && l.QuantityChange > 0)
                .SumAsync(l => (decimal?)l.QuantityChange) ?? 0;
            var totalDistributed = await _context.InventoryLogs
                .Where(l => l.ProductId == productId && l.Type == InventoryLogType.Distribution)
                .SumAsync(l => (decimal?)-l.QuantityChange) ?? 0;

            var openBatches = await _context.SupplyReceiptItems
                .Include(i => i.SupplyReceipt)
                .Where(i => i.ProductId == productId && i.RemainingQuantity > 0)
                .ToListAsync();

            var oldest = openBatches
                .OrderBy(i => i.ExpiryDate.HasValue ? 0 : 1)
                .ThenBy(i => i.ExpiryDate ?? DateTime.MaxValue)
                .ThenBy(i => i.SupplyReceipt?.Date ?? DateTime.MaxValue)
                .FirstOrDefault();

            return Json(new
            {
                success = true,
                name = string.IsNullOrWhiteSpace(p.NameAr) ? p.Name : p.NameAr,
                unit = string.IsNullOrWhiteSpace(p.Unit) ? "قطعة" : p.Unit,
                packaging = p.PackagingName,
                unitsPerPackage = perPack,
                servingName = p.ServingName,
                servingsPerUnit = p.ServingsPerUnit,
                inWarehouse = p.StockQuantity,
                inWarehousePacks = perPack > 1 ? p.StockQuantity / perPack : (decimal?)null,
                totalReceived,
                totalDistributed,
                costPrice = p.CostPrice,
                suggestedPrice = p.Price,
                receiptCount = receipts.Count,
                receipts,
                oldestBatch = oldest == null ? null : new
                {
                    remaining = oldest.RemainingQuantity,
                    receiptNumber = oldest.SupplyReceipt?.ReceiptNumber,
                    expiryDate = oldest.ExpiryDate,
                    daysLeft = oldest.ExpiryDate.HasValue
                        ? (int)(oldest.ExpiryDate.Value.Date - DateTime.Today).TotalDays
                        : (int?)null
                },
                branches = p.ProductBranches.Select(pb => new
                {
                    branchId = pb.BranchId,
                    branch = pb.Branch!.NameAr,
                    stock = pb.StockQuantity,
                    sellable = pb.IsSellable,
                    price = pb.OverridePrice
                })
            });
        }

        // ═══════════════════ EXPIRY / FIFO ALERTS ═══════════════════
        // Batches listed in the exact order they should be consumed, so the admin can
        // push the oldest stock out first.
        public async Task<IActionResult> Expiry(int? warnDays)
        {
            var warn = warnDays ?? 14;
            var today = DateTime.Today;

            var batches = await _inventory.OpenBatches().ToListAsync();

            var rows = batches.Select(b => new ExpiryRow
            {
                ProductId = b.ProductId,
                Product = string.IsNullOrWhiteSpace(b.ProductNameSnapshot)
                    ? (b.Product?.NameAr ?? b.Product?.Name ?? "—")
                    : b.ProductNameSnapshot,
                Unit = string.IsNullOrWhiteSpace(b.UnitSnapshot) ? (b.Product?.Unit ?? "") : b.UnitSnapshot,
                ReceiptNumber = b.SupplyReceipt?.ReceiptNumber,
                ReceiptDate = b.SupplyReceipt?.Date,
                Supplier = b.SupplyReceipt?.Supplier?.Name ?? b.SupplyReceipt?.SupplierNameSnapshot,
                BatchNumber = b.BatchNumber,
                ExpiryDate = b.ExpiryDate,
                Remaining = b.RemainingQuantity,
                Received = b.BaseQuantity,
                CostPerBaseUnit = b.CostPerBaseUnit,
                WarnDays = b.Product?.ExpiryWarningDays ?? warn
            })
            // FIFO order: soonest expiry first, undated batches last
            .OrderBy(r => r.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(r => r.ExpiryDate ?? DateTime.MaxValue)
            .ThenBy(r => r.ReceiptDate ?? DateTime.MaxValue)
            .ToList();

            ViewBag.WarnDays = warn;
            ViewBag.ExpiredValue = rows.Where(r => r.IsExpired).Sum(r => r.Remaining * r.CostPerBaseUnit);
            ViewBag.SoonValue = rows.Where(r => r.IsExpiringSoon).Sum(r => r.Remaining * r.CostPerBaseUnit);
            ViewBag.NoDateCount = rows.Count(r => !r.ExpiryDate.HasValue);

            return View(rows);
        }

        // ═══════════════════ VISIBILITY DIAGNOSTIC ═══════════════════
        // Answers "why isn't this item showing on the cashier screen?" by evaluating
        // every condition the cashier query applies, one at a time.
        public async Task<IActionResult> WhyNotVisible(int? branchId)
        {
            var branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            var bid = branchId ?? branches.FirstOrDefault()?.Id ?? 0;

            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches)
                .OrderBy(p => p.NameAr)
                .ToListAsync();

            var sysTaxStr = await _context.SystemSettings
                .Where(s => s.Key == "TaxRate" && (s.BranchId == bid || s.BranchId == null))
                .OrderByDescending(s => s.BranchId.HasValue)
                .Select(s => s.Value).FirstOrDefaultAsync() ?? "14";
            decimal.TryParse(sysTaxStr, out var sysTax);

            var rows = products.Select(p =>
            {
                var pb = p.ProductBranches?.FirstOrDefault(x => x.BranchId == bid);
                var basePrice = pb?.OverridePrice
                    ?? (p.SellByBox && p.UnitsPerBox > 0 ? p.BoxSellPrice / p.UnitsPerBox : p.Price);
                var taxRate = pb?.TaxRateOverride ?? p.TaxRateOverride ?? sysTax;

                var reasons = new List<string>();
                if (!p.IsActive) reasons.Add("الصنف موقوف (IsActive = false) — فعّله من صفحة المنتجات");
                if (!p.IsAvailable) reasons.Add("الصنف غير متاح (Product.IsAvailable = false)");
                if (pb == null) reasons.Add("لم يُوزَّع على هذا الفرع إطلاقًا — وزّعه من شاشة التوزيع");
                else
                {
                    if (!pb.IsSellable) reasons.Add("موزَّع كـ«خامة» مش «للبيع» — أعد توزيعه واختر 🛒 يتباع بالوحدة");
                    if (!pb.IsAvailable) reasons.Add("متوقف في هذا الفرع (ProductBranch.IsAvailable = false)");
                }

                return new VisibilityRow
                {
                    ProductId = p.Id,
                    Product = string.IsNullOrWhiteSpace(p.NameAr) ? p.Name : p.NameAr,
                    Category = p.Category?.NameAr ?? "—",
                    IsActive = p.IsActive,
                    ProductAvailable = p.IsAvailable,
                    HasBranchRow = pb != null,
                    BranchSellable = pb?.IsSellable,
                    BranchAvailable = pb?.IsAvailable,
                    BranchStock = pb?.StockQuantity ?? 0,
                    Unit = string.IsNullOrWhiteSpace(p.Unit) ? "قطعة" : p.Unit,
                    SellingPrice = basePrice,
                    FinalPrice = Math.Round(basePrice * (1 + taxRate / 100), MidpointRounding.AwayFromZero),
                    Reasons = reasons
                };
            }).ToList();

            ViewBag.Branches = branches;
            ViewBag.BranchId = bid;
            ViewBag.BranchName = branches.FirstOrDefault(b => b.Id == bid)?.NameAr ?? "—";
            return View(rows);
        }

        // Load the current flags for one product/branch pair (for the edit dialog)
        [HttpGet]
        public async Task<IActionResult> GetVisibility(int productId, int branchId)
        {
            var p = await _context.Products.FindAsync(productId);
            if (p == null) return Json(new { success = false, message = "الصنف غير موجود" });

            var pb = await _context.ProductBranches
                .FirstOrDefaultAsync(x => x.ProductId == productId && x.BranchId == branchId);

            return Json(new
            {
                success = true,
                productId,
                branchId,
                name = string.IsNullOrWhiteSpace(p.NameAr) ? p.Name : p.NameAr,
                isActive = p.IsActive,
                isAvailable = p.IsAvailable,
                hasBranchRow = pb != null,
                isSellable = pb?.IsSellable ?? true,
                branchAvailable = pb?.IsAvailable ?? true,
                stock = pb?.StockQuantity ?? 0,
                unit = pb?.StockUnitLabel ?? (string.IsNullOrWhiteSpace(p.Unit) ? "قطعة" : p.Unit),
                price = pb?.OverridePrice ?? p.Price,
                minAlert = pb?.MinStockAlertOverride ?? p.MinStockAlert
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SaveVisibility([FromBody] VisibilityEditRequest req)
        {
            var p = await _context.Products.FindAsync(req.ProductId);
            if (p == null) return Json(new { success = false, message = "الصنف غير موجود" });

            p.IsActive = req.IsActive;
            p.IsAvailable = req.IsAvailable;

            var pb = await _context.ProductBranches
                .FirstOrDefaultAsync(x => x.ProductId == req.ProductId && x.BranchId == req.BranchId);

            if (pb == null)
            {
                // Allow enabling an item at a branch before any stock has been sent,
                // so the cashier can see it (at zero) instead of it being invisible.
                if (!req.CreateBranchRow)
                    return Json(new
                    {
                        success = false,
                        message = "الصنف لم يُوزَّع على هذا الفرع — وزّعه أولًا من شاشة التوزيع"
                    });

                pb = new ProductBranch
                {
                    ProductId = req.ProductId,
                    BranchId = req.BranchId,
                    StockQuantity = 0,
                    StockUnitLabel = string.IsNullOrWhiteSpace(p.Unit) ? "قطعة" : p.Unit
                };
                _context.ProductBranches.Add(pb);
            }

            pb.IsSellable = req.IsSellable;
            pb.IsAvailable = req.BranchAvailable;
            if (req.SellingPrice.HasValue && req.SellingPrice > 0) pb.OverridePrice = req.SellingPrice;
            if (req.MinStockAlert.HasValue) pb.MinStockAlertOverride = req.MinStockAlert;

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // One-click: clear every flag that is blocking this item at this branch
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> MakeVisible([FromBody] VisibilityEditRequest req)
        {
            var p = await _context.Products.FindAsync(req.ProductId);
            if (p == null) return Json(new { success = false, message = "الصنف غير موجود" });

            var pb = await _context.ProductBranches
                .FirstOrDefaultAsync(x => x.ProductId == req.ProductId && x.BranchId == req.BranchId);
            if (pb == null)
                return Json(new
                {
                    success = false,
                    message = "الصنف لم يُوزَّع على هذا الفرع — لازم توزّعه أولًا",
                    needsDistribution = true
                });

            p.IsActive = true;
            p.IsAvailable = true;
            pb.IsSellable = true;
            pb.IsAvailable = true;

            await _context.SaveChangesAsync();

            var priceMissing = (pb.OverridePrice ?? p.Price) <= 0;
            return Json(new
            {
                success = true,
                priceMissing,
                message = priceMissing
                    ? "الصنف بقى ظاهر — بس سعره صفر، حدّد سعر البيع"
                    : "الصنف بقى ظاهر عند الكاشير"
            });
        }

        // ═══════════════════ PRODUCT LEDGER (كارت الصنف) ═══════════════════
        public async Task<IActionResult> ProductCard(int id, DateTime? from, DateTime? to)
        {
            var product = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches).ThenInclude(pb => pb.Branch)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            from ??= DateTime.Today.AddDays(-30);
            to ??= DateTime.Today;

            var opening = await _inventory.GetOpeningBalanceAsync(id, from.Value);
            var logs = await _inventory.GetLedgerAsync(id, from.Value, to.Value);

            var creatorIds = logs.Where(l => l.CreatedById != null).Select(l => l.CreatedById!).Distinct().ToList();
            var creators = await _context.Users.Where(u => creatorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.FullNameAr) ? (u.UserName ?? "—") : u.FullNameAr);

            var rows = new List<LedgerRow>();
            var running = opening;
            foreach (var l in logs)
            {
                running += l.QuantityChange;
                rows.Add(new LedgerRow
                {
                    Date = l.CreatedAt,
                    Type = l.Type,
                    Reference = l.ReceiptNumber,
                    Description = l.Reason,
                    Branch = l.Branch?.NameAr,
                    In = l.QuantityChange > 0 ? l.QuantityChange : 0,
                    Out = l.QuantityChange < 0 ? -l.QuantityChange : 0,
                    Balance = running,
                    Value = l.MovementValue,
                    EnteredQuantity = l.EnteredQuantity,
                    EnteredAsPackaging = l.EnteredAsPackaging,
                    By = l.CreatedById != null ? creators.GetValueOrDefault(l.CreatedById, "—") : "—"
                });
            }

            ViewBag.Product = product;
            ViewBag.From = from.Value;
            ViewBag.To = to.Value;
            ViewBag.Opening = opening;
            ViewBag.Closing = running;

            return View(rows);
        }

        // ═══════════════════ MAIN REPORT ═══════════════════
        public async Task<IActionResult> Report(DateTime? from, DateTime? to)
        {
            from ??= DateTime.Today;
            to ??= from;

            var logs = await _context.InventoryLogs
                .Include(l => l.Product)
                .Include(l => l.Branch)
                .Where(l => l.CreatedAt.Date >= from.Value.Date && l.CreatedAt.Date <= to.Value.Date)
                .OrderBy(l => l.CreatedAt)
                .ToListAsync();

            var creatorIds = logs.Where(l => l.CreatedById != null).Select(l => l.CreatedById!).Distinct().ToList();
            var creators = await _context.Users.Where(u => creatorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.FullNameAr) ? (u.UserName ?? "—") : u.FullNameAr);
            string By(string? id) => id != null ? creators.GetValueOrDefault(id, "—") : "—";
            string PName(Product? p) => p == null ? "—" : (string.IsNullOrEmpty(p.NameAr) ? p.Name : p.NameAr);
            string Qty(InventoryLog l)
            {
                var baseQty = Math.Abs(l.QuantityChange);
                var unit = l.Product?.Unit ?? "";
                if (l.EnteredAsPackaging != null && l.EnteredQuantity.HasValue)
                    return $"{l.EnteredQuantity.Value:0.###} {l.EnteredAsPackaging} = {baseQty:0.###} {unit}";
                return $"{baseQty:0.###} {unit}";
            }

            var receipts = await _context.SupplyReceipts
                .Include(r => r.Supplier)
                .Where(r => r.Date.Date >= from.Value.Date && r.Date.Date <= to.Value.Date)
                .ToListAsync();

            // Per-product movement summary — the core accountant view
            var productIds = logs.Select(l => l.ProductId).Distinct().ToList();
            var summary = new List<ProductMovementRow>();
            foreach (var pid in productIds)
            {
                var product = logs.First(l => l.ProductId == pid).Product;
                var opening = await _inventory.GetOpeningBalanceAsync(pid, from.Value);
                var plogs = logs.Where(l => l.ProductId == pid).ToList();
                var inQty = plogs.Where(l => l.QuantityChange > 0).Sum(l => l.QuantityChange);
                var outQty = plogs.Where(l => l.QuantityChange < 0).Sum(l => -l.QuantityChange);
                var closing = opening + inQty - outQty;

                summary.Add(new ProductMovementRow
                {
                    ProductId = pid,
                    Product = PName(product),
                    Unit = product?.Unit ?? "",
                    Opening = opening,
                    In = inQty,
                    Out = outQty,
                    Closing = closing,
                    CostPrice = product?.CostPrice ?? 0,
                    ClosingValue = closing * (product?.CostPrice ?? 0)
                });
            }

            var model = new InventoryReportViewModel
            {
                From = from.Value,
                To = to.Value,
                TotalPurchases = receipts.Sum(r => r.TotalCost),
                ReceiptCount = receipts.Count,
                UnpaidToSuppliers = receipts.Where(r => !r.IsPaid).Sum(r => r.TotalCost - r.AmountPaid),
                TotalDistributedValue = logs.Where(l => l.Type == InventoryLogType.Distribution).Sum(l => l.MovementValue ?? 0),
                TotalWasteValue = logs.Where(l => l.Type == InventoryLogType.Waste).Sum(l => l.MovementValue ?? 0),
                Summary = summary.OrderByDescending(s => s.ClosingValue).ToList(),

                Supply = logs.Where(l => l.Type == InventoryLogType.Supply).Select(l => new SupplyLogRow
                {
                    CreatedAt = l.CreatedAt,
                    ReceiptNumber = l.ReceiptNumber,
                    Product = PName(l.Product),
                    QuantityText = Qty(l),
                    UnitCost = l.UnitCost,
                    LineValue = l.MovementValue,
                    SupplierName = l.SupplierName,
                    Notes = l.Notes,
                    By = By(l.CreatedById)
                }).ToList(),

                Distribution = logs.Where(l => l.Type == InventoryLogType.Distribution).Select(l => new DistributionLogRow
                {
                    CreatedAt = l.CreatedAt,
                    Product = PName(l.Product),
                    Branch = l.Branch?.NameAr ?? "—",
                    QuantityText = Qty(l),
                    ExpectedDurationDays = l.ExpectedDurationDays,
                    DailyCost = (l.ExpectedDurationDays.HasValue && l.ExpectedDurationDays > 0)
                        ? (l.MovementValue ?? 0) / l.ExpectedDurationDays.Value
                        : (decimal?)null,
                    LineValue = l.MovementValue,
                    Notes = l.Notes,
                    By = By(l.CreatedById)
                }).ToList(),

                NewProducts = logs.Where(l => l.Type == InventoryLogType.NewProduct).Select(l => new NewProductLogRow
                {
                    CreatedAt = l.CreatedAt,
                    Product = PName(l.Product),
                    QuantityText = Qty(l),
                    By = By(l.CreatedById)
                }).ToList(),

                Adjustments = logs.Where(l => l.Type == InventoryLogType.Adjustment
                                           || l.Type == InventoryLogType.Waste
                                           || l.Type == InventoryLogType.ReturnToWarehouse)
                    .Select(l => new AdjustmentLogRow
                    {
                        CreatedAt = l.CreatedAt,
                        Product = PName(l.Product),
                        Type = l.Type,
                        Branch = l.Branch?.NameAr,
                        QuantityText = (l.QuantityChange < 0 ? "-" : "+") + Qty(l),
                        LineValue = l.MovementValue,
                        Reason = l.Reason,
                        By = By(l.CreatedById)
                    }).ToList()
            };

            return View(model);
        }
    }

    // ═══════════════════ REQUEST DTOs ═══════════════════
    // NOTE: IdRequest is already declared in BranchController.cs (same namespace) — reused here.

    public class SupplierRequest
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class ReceiptLineDto
    {
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
        public bool AsPackage { get; set; }
        public decimal UnitCost { get; set; }
        public string? Unit { get; set; }
        public string? PackagingName { get; set; }
        public decimal? UnitsPerPackage { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public DateTime? ProductionDate { get; set; }
        public string? BatchNumber { get; set; }
        public int? ShelfLifeDays { get; set; }
        public string? ServingName { get; set; }
        public decimal? ServingsPerUnit { get; set; }
    }

    public class CreateReceiptRequest
    {
        public DateTime? Date { get; set; }
        public int? SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public string? InvoiceNumber { get; set; }
        public string? Notes { get; set; }
        public bool IsPaid { get; set; } = true;
        public decimal AmountPaid { get; set; }
        public List<ReceiptLineDto>? Lines { get; set; }
    }

    public class SettleReceiptRequest
    {
        public int ReceiptId { get; set; }
        public decimal Amount { get; set; }
    }

    public class DistributeRequest
    {
        public int ProductId { get; set; }
        public int BranchId { get; set; }
        public decimal Quantity { get; set; }
        public bool AsPackage { get; set; }
        public int? ExpectedDurationDays { get; set; }
        public bool IsSellable { get; set; } = true;
        public string? Notes { get; set; }

        // Commercial settings applied to the receiving branch
        public decimal? SellingPrice { get; set; }
        public decimal? TaxRateOverride { get; set; }
        public bool? SkipKitchenOverride { get; set; }
        public int? MinStockAlert { get; set; }
        public bool IsAvailable { get; set; } = true;
    }

    public class AdjustRequest
    {
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
        public bool AsPackage { get; set; }
        public string Type { get; set; } = "Adjustment";
        public string? Reason { get; set; }
        public int? BranchId { get; set; }
    }

    // ═══════════════════ VIEW MODELS ═══════════════════
    public class InventoryReportViewModel
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public decimal TotalPurchases { get; set; }
        public int ReceiptCount { get; set; }
        public decimal UnpaidToSuppliers { get; set; }
        public decimal TotalDistributedValue { get; set; }
        public decimal TotalWasteValue { get; set; }
        public List<ProductMovementRow> Summary { get; set; } = new();
        public List<SupplyLogRow> Supply { get; set; } = new();
        public List<DistributionLogRow> Distribution { get; set; } = new();
        public List<NewProductLogRow> NewProducts { get; set; } = new();
        public List<AdjustmentLogRow> Adjustments { get; set; } = new();
    }

    public class ProductMovementRow
    {
        public int ProductId { get; set; }
        public string Product { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Opening { get; set; }
        public decimal In { get; set; }
        public decimal Out { get; set; }
        public decimal Closing { get; set; }
        public decimal CostPrice { get; set; }
        public decimal ClosingValue { get; set; }
    }

    public class SupplyLogRow
    {
        public DateTime CreatedAt { get; set; }
        public string? ReceiptNumber { get; set; }
        public string Product { get; set; } = "";
        public string QuantityText { get; set; } = "";
        public decimal? UnitCost { get; set; }
        public decimal? LineValue { get; set; }
        public string? SupplierName { get; set; }
        public string? Notes { get; set; }
        public string By { get; set; } = "";
    }

    public class DistributionLogRow
    {
        public DateTime CreatedAt { get; set; }
        public string Product { get; set; } = "";
        public string Branch { get; set; } = "";
        public string QuantityText { get; set; } = "";
        public int? ExpectedDurationDays { get; set; }
        public decimal? DailyCost { get; set; }
        public decimal? LineValue { get; set; }
        public string? Notes { get; set; }
        public string By { get; set; } = "";
    }

    public class NewProductLogRow
    {
        public DateTime CreatedAt { get; set; }
        public string Product { get; set; } = "";
        public string QuantityText { get; set; } = "";
        public string By { get; set; } = "";
    }

    public class AdjustmentLogRow
    {
        public DateTime CreatedAt { get; set; }
        public string Product { get; set; } = "";
        public InventoryLogType Type { get; set; }
        public string? Branch { get; set; }
        public string QuantityText { get; set; } = "";
        public decimal? LineValue { get; set; }
        public string Reason { get; set; } = "";
        public string By { get; set; } = "";
    }

    public class VisibilityEditRequest
    {
        public int ProductId { get; set; }
        public int BranchId { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsAvailable { get; set; } = true;
        public bool IsSellable { get; set; } = true;
        public bool BranchAvailable { get; set; } = true;
        public decimal? SellingPrice { get; set; }
        public int? MinStockAlert { get; set; }
        public bool CreateBranchRow { get; set; }
    }

    public class VisibilityRow
    {
        public int ProductId { get; set; }
        public string Product { get; set; } = "";
        public string Category { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool IsActive { get; set; }
        public bool ProductAvailable { get; set; }
        public bool HasBranchRow { get; set; }
        public bool? BranchSellable { get; set; }
        public bool? BranchAvailable { get; set; }
        public decimal BranchStock { get; set; }
        public decimal SellingPrice { get; set; }
        public decimal FinalPrice { get; set; }
        public List<string> Reasons { get; set; } = new();
        public bool Visible => !Reasons.Any();
    }

    public class BranchStockRow
    {
        public string Product { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Branch { get; set; } = "";
        public decimal Quantity { get; set; }
        public bool IsSellable { get; set; }
        public decimal Value { get; set; }
        public DateTime? LastDistributedAt { get; set; }
    }

    public class ExpiryRow
    {
        public int ProductId { get; set; }
        public string Product { get; set; } = "";
        public string Unit { get; set; } = "";
        public string? ReceiptNumber { get; set; }
        public DateTime? ReceiptDate { get; set; }
        public string? Supplier { get; set; }
        public string? BatchNumber { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public decimal Remaining { get; set; }
        public decimal Received { get; set; }
        public decimal CostPerBaseUnit { get; set; }
        public int WarnDays { get; set; } = 14;

        public decimal Value => Remaining * CostPerBaseUnit;
        public int? DaysLeft => ExpiryDate.HasValue
            ? (int)(ExpiryDate.Value.Date - DateTime.Today).TotalDays : (int?)null;
        public bool IsExpired => DaysLeft.HasValue && DaysLeft < 0;
        public bool IsExpiringSoon => DaysLeft.HasValue && DaysLeft >= 0 && DaysLeft <= WarnDays;
    }

    public class LedgerRow
    {
        public DateTime Date { get; set; }
        public InventoryLogType Type { get; set; }
        public string? Reference { get; set; }
        public string Description { get; set; } = "";
        public string? Branch { get; set; }
        public decimal In { get; set; }
        public decimal Out { get; set; }
        public decimal Balance { get; set; }
        public decimal? Value { get; set; }
        public decimal? EnteredQuantity { get; set; }
        public string? EnteredAsPackaging { get; set; }
        public string By { get; set; } = "";
    }
}
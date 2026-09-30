using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;

namespace RestaurantERP.Services
{
    /// <summary>
    /// Single source of truth for warehouse movements.
    ///
    /// Model: ONE central warehouse that feeds N branches.
    ///   Product.StockQuantity        → base units still sitting centrally (undistributed)
    ///   ProductBranch.StockQuantity  → base units currently held by that branch
    ///   InventoryLog                 → append-only ledger of every movement
    ///
    /// Everything is stored in the product's BASE unit (Unit). Packaging
    /// (PackagingName + UnitsPerPackage) is purely an input/display convenience:
    /// "30 كرتونة" of a 12-per-carton product becomes 360 base units in the ledger.
    /// </summary>
    public class InventoryService
    {
        private readonly ApplicationDbContext _context;

        public InventoryService(ApplicationDbContext context) => _context = context;

        // ─────────────────────────────────────────────────────────
        // PACKAGING CONVERSION
        // ─────────────────────────────────────────────────────────
        public static decimal ToBaseUnits(Product product, decimal enteredQty, bool enteredAsPackage)
            => enteredAsPackage && product.UnitsPerPackage > 0
                ? enteredQty * product.UnitsPerPackage
                : enteredQty;

        /// <summary>Human-readable quantity, e.g. "360 زجاجة (30 كرتونة)".</summary>
        public static string Describe(Product product, decimal baseQty)
        {
            var baseText = $"{Trim(baseQty)} {product.Unit}";
            if (!product.HasPackaging) return baseText;
            var packs = baseQty / product.UnitsPerPackage;
            return $"{baseText} ({Trim(packs)} {product.PackagingName})";
        }

        private static string Trim(decimal d) => d.ToString("0.###");

        // ─────────────────────────────────────────────────────────
        // RECEIPTS (الوارد)
        // ─────────────────────────────────────────────────────────
        public async Task<string> GenerateReceiptNumberAsync(DateTime date)
        {
            var prefix = $"GRN-{date:yyyyMMdd}-";
            var todayCount = await _context.SupplyReceipts.CountAsync(r => r.ReceiptNumber.StartsWith(prefix));
            // Guard against gaps/collisions if a receipt was deleted
            for (var n = todayCount + 1; ; n++)
            {
                var candidate = $"{prefix}{n:D3}";
                if (!await _context.SupplyReceipts.AnyAsync(r => r.ReceiptNumber == candidate))
                    return candidate;
            }
        }

        public class ReceiptLineInput
        {
            public int ProductId { get; set; }
            public decimal Quantity { get; set; }
            public bool AsPackage { get; set; }
            public decimal CostPerEnteredUnit { get; set; }

            // Optional master-data set on the fly while receiving. Products created before
            // the warehouse module existed have no unit, and the storekeeper often knows the
            // packaging only when the goods physically arrive.
            public string? Unit { get; set; }
            public string? PackagingName { get; set; }
            public decimal? UnitsPerPackage { get; set; }

            public DateTime? ExpiryDate { get; set; }
            public DateTime? ProductionDate { get; set; }
            public string? BatchNumber { get; set; }
            public int? ShelfLifeDays { get; set; }

            // Optional: "كيلوجرام واحد = 10 كباية"
            public string? ServingName { get; set; }
            public decimal? ServingsPerUnit { get; set; }
        }

        /// <summary>Records a goods-received note: raises central stock and writes the ledger.</summary>
        public async Task<SupplyReceipt> CreateReceiptAsync(
            IEnumerable<ReceiptLineInput> lines,
            DateTime date,
            int? supplierId,
            string? supplierNameFallback,
            string? invoiceNumber,
            string? notes,
            bool isPaid,
            decimal amountPaid,
            string? userId)
        {
            var lineList = lines.ToList();
            if (!lineList.Any()) throw new InvalidOperationException("لا توجد أصناف في الإيصال");

            var productIds = lineList.Select(l => l.ProductId).Distinct().ToList();
            var products = await _context.Products.Where(p => productIds.Contains(p.Id))
                                                  .ToDictionaryAsync(p => p.Id);

            Supplier? supplier = supplierId.HasValue
                ? await _context.Suppliers.FindAsync(supplierId.Value)
                : null;

            var receipt = new SupplyReceipt
            {
                ReceiptNumber = await GenerateReceiptNumberAsync(date),
                Date = date,
                SupplierId = supplier?.Id,
                SupplierNameSnapshot = supplier?.Name ?? supplierNameFallback,
                InvoiceNumber = invoiceNumber,
                Notes = notes,
                CreatedById = userId,
                CreatedAt = DateTime.Now
            };
            _context.SupplyReceipts.Add(receipt);

            decimal receiptTotal = 0;

            foreach (var line in lineList)
            {
                if (line.Quantity <= 0) continue;
                if (!products.TryGetValue(line.ProductId, out var product)) continue;

                // ── Apply/repair master data supplied on this line ──────────────
                if (!string.IsNullOrWhiteSpace(line.Unit))
                    product.Unit = line.Unit.Trim();
                if (string.IsNullOrWhiteSpace(product.Unit))
                    product.Unit = "قطعة";               // never leave it blank

                if (!string.IsNullOrWhiteSpace(line.PackagingName))
                    product.PackagingName = line.PackagingName.Trim();
                if (line.UnitsPerPackage.HasValue && line.UnitsPerPackage.Value > 0)
                    product.UnitsPerPackage = line.UnitsPerPackage.Value;
                if (product.UnitsPerPackage <= 0)
                    product.UnitsPerPackage = 1;          // legacy rows migrated in as 0

                var baseQty = ToBaseUnits(product, line.Quantity, line.AsPackage);
                if (baseQty <= 0) continue;

                var unitsPerPackage = product.UnitsPerPackage > 0 ? product.UnitsPerPackage : 1;
                var costPerBase = line.AsPackage && unitsPerPackage > 0
                    ? line.CostPerEnteredUnit / unitsPerPackage
                    : line.CostPerEnteredUnit;
                var lineTotal = Math.Round(costPerBase * baseQty, 2);
                receiptTotal += lineTotal;

                receipt.Items.Add(new SupplyReceiptItem
                {
                    ProductId = product.Id,
                    ProductNameSnapshot = string.IsNullOrEmpty(product.NameAr) ? product.Name : product.NameAr,
                    UnitSnapshot = product.Unit,
                    EnteredQuantity = line.Quantity,
                    EnteredAsPackaging = line.AsPackage ? product.PackagingName : null,
                    UnitsPerPackageSnapshot = unitsPerPackage,
                    BaseQuantity = baseQty,
                    CostPerEnteredUnit = line.CostPerEnteredUnit,
                    CostPerBaseUnit = costPerBase,
                    LineTotal = lineTotal,
                    ExpiryDate = line.ExpiryDate,
                    ProductionDate = line.ProductionDate,
                    BatchNumber = line.BatchNumber,
                    RemainingQuantity = baseQty      // nothing consumed from this batch yet
                });

                if (line.ShelfLifeDays.HasValue && line.ShelfLifeDays > 0)
                    product.ShelfLifeDays = line.ShelfLifeDays;
                if (!string.IsNullOrWhiteSpace(line.ServingName))
                    product.ServingName = line.ServingName.Trim();
                if (line.ServingsPerUnit.HasValue && line.ServingsPerUnit.Value > 0)
                    product.ServingsPerUnit = line.ServingsPerUnit.Value;

                var before = product.StockQuantity;
                product.StockQuantity += baseQty;
                product.TrackStock = true;
                if (costPerBase > 0) product.CostPrice = Math.Round(costPerBase, 2);

                _context.InventoryLogs.Add(new InventoryLog
                {
                    ProductId = product.Id,
                    QuantityChange = baseQty,
                    QuantityBefore = before,
                    QuantityAfter = product.StockQuantity,
                    Reason = supplier != null ? $"توريد من {supplier.Name}" : "توريد مخزون",
                    Type = InventoryLogType.Supply,
                    SupplyReceipt = receipt,
                    ReceiptNumber = receipt.ReceiptNumber,
                    EnteredQuantity = line.Quantity,
                    EnteredAsPackaging = line.AsPackage ? product.PackagingName : null,
                    UnitCost = Math.Round(costPerBase, 2),
                    MovementValue = lineTotal,
                    SupplierName = receipt.SupplierNameSnapshot,
                    Notes = notes,
                    CreatedAt = date,
                    CreatedById = userId
                });
            }

            if (!receipt.Items.Any())
                throw new InvalidOperationException("لا توجد أصناف صالحة في الإيصال");

            receipt.TotalCost = receiptTotal;
            receipt.IsPaid = isPaid;
            receipt.AmountPaid = isPaid ? receiptTotal : Math.Min(amountPaid, receiptTotal);
            receipt.PaidAt = isPaid ? date : null;

            await _context.SaveChangesAsync();
            return receipt;
        }

        // ─────────────────────────────────────────────────────────
        // DISTRIBUTION (المنصرف)
        // ─────────────────────────────────────────────────────────
        /// <summary>Commercial settings applied to the branch when stock is sent to it.</summary>
        public class DistributionPricing
        {
            public decimal? SellingPrice { get; set; }
            public decimal? TaxRateOverride { get; set; }
            public bool? SkipKitchenOverride { get; set; }
            public int? MinStockAlert { get; set; }
            public bool IsAvailable { get; set; } = true;
        }

        /// <summary>
        /// Draws <paramref name="baseQty"/> from this product's batches, oldest expiry
        /// first (FIFO). Batches with no expiry are consumed last, ordered by receipt date.
        /// Returns a human-readable summary of what was pulled.
        /// </summary>
        private async Task<string?> ConsumeBatchesFifoAsync(int productId, decimal baseQty)
        {
            var batches = await _context.SupplyReceiptItems
                .Include(i => i.SupplyReceipt)
                .Where(i => i.ProductId == productId && i.RemainingQuantity > 0)
                .ToListAsync();

            var ordered = batches
                .OrderBy(i => i.ExpiryDate.HasValue ? 0 : 1)            // dated batches first
                .ThenBy(i => i.ExpiryDate ?? DateTime.MaxValue)          // soonest expiry
                .ThenBy(i => i.SupplyReceipt?.Date ?? DateTime.MaxValue) // then oldest receipt
                .ToList();

            var remaining = baseQty;
            var used = new List<string>();

            foreach (var b in ordered)
            {
                if (remaining <= 0) break;
                var take = Math.Min(b.RemainingQuantity, remaining);
                b.RemainingQuantity -= take;
                remaining -= take;
                used.Add(b.ExpiryDate.HasValue
                    ? $"{take:0.###} من دفعة تنتهي {b.ExpiryDate:yyyy-MM-dd}"
                    : $"{take:0.###} من دفعة {b.SupplyReceipt?.ReceiptNumber ?? "—"}");
            }

            return used.Any() ? string.Join("، ", used) : null;
        }

        public class DistributionResult
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public decimal NewCentralStock { get; set; }
            public decimal NewBranchStock { get; set; }
            public decimal BaseQuantity { get; set; }
            public decimal MovementValue { get; set; }
            public string? BatchNote { get; set; }
            public string? BranchUnitLabel { get; set; }
            public decimal BranchQuantityAdded { get; set; }
        }

        public async Task<DistributionResult> DistributeAsync(
            int productId, int branchId, decimal quantity, bool asPackage,
            int? expectedDurationDays, string? notes, string? userId,
            bool isSellable = true, DistributionPricing? pricing = null)
        {
            var product = await _context.Products.FindAsync(productId);
            if (product == null) return new DistributionResult { Success = false, Message = "المنتج غير موجود" };

            var branch = await _context.Branches.FindAsync(branchId);
            if (branch == null) return new DistributionResult { Success = false, Message = "الفرع غير موجود" };

            var baseQty = ToBaseUnits(product, quantity, asPackage);
            if (baseQty <= 0) return new DistributionResult { Success = false, Message = "الكمية يجب أن تكون أكبر من صفر" };

            if (product.StockQuantity < baseQty)
                return new DistributionResult
                {
                    Success = false,
                    Message = $"الكمية المتاحة بالمخزن ({Describe(product, product.StockQuantity)}) أقل من المطلوب توزيعه ({Describe(product, baseQty)})"
                };

            var before = product.StockQuantity;
            product.StockQuantity -= baseQty;

            // Pull from the oldest-expiring batches first
            var batchNote = await ConsumeBatchesFifoAsync(productId, baseQty);

            var pb = await _context.ProductBranches
                .FirstOrDefaultAsync(x => x.ProductId == productId && x.BranchId == branchId);
            if (pb == null)
            {
                pb = new ProductBranch { ProductId = productId, BranchId = branchId, StockQuantity = 0 };
                _context.ProductBranches.Add(pb);
            }
            // A bulk product sold by the portion lands on the branch shelf as servings:
            // 2 كيلوجرام at 10 كباية/كجم becomes 20 كباية for the cashier to sell.
            var useServings = isSellable && product.HasServings;
            var branchQty = useServings ? baseQty * product.ServingsPerUnit : baseQty;

            pb.StockQuantity += branchQty;
            pb.StockUnitLabel = useServings ? product.ServingName : product.Unit;
            pb.ServingsPerUnitSnapshot = useServings ? product.ServingsPerUnit : 1;
            pb.IsSellable = isSellable;
            pb.ExpectedDurationDays = expectedDurationDays;
            pb.LastDistributedAt = DateTime.Now;

            // Availability is ALWAYS recomputed from this distribution. Previously it was
            // only touched when pricing was supplied, so an item distributed once as an
            // ingredient (which sets false) stayed hidden forever even after it was
            // redistributed as sellable — silently invisible at the cashier.
            pb.IsAvailable = isSellable && (pricing?.IsAvailable ?? true);

            if (isSellable && pricing != null)
            {
                if (pricing.SellingPrice.HasValue && pricing.SellingPrice > 0)
                    pb.OverridePrice = pricing.SellingPrice;
                pb.TaxRateOverride = pricing.TaxRateOverride;
                pb.SkipKitchenOverride = pricing.SkipKitchenOverride;
                pb.MinStockAlertOverride = pricing.MinStockAlert;
            }

            var movementValue = Math.Round(product.CostPrice * baseQty, 2);

            _context.InventoryLogs.Add(new InventoryLog
            {
                ProductId = productId,
                BranchId = branchId,
                QuantityChange = -baseQty,
                QuantityBefore = before,
                QuantityAfter = product.StockQuantity,
                Reason = $"توزيع إلى فرع {branch.NameAr}" + (isSellable ? " (للبيع)" : " (خامة للتصنيع)"),
                Type = InventoryLogType.Distribution,
                DistributedAsSellable = isSellable,
                EnteredQuantity = quantity,
                EnteredAsPackaging = asPackage ? product.PackagingName : null,
                ExpectedDurationDays = expectedDurationDays,
                UnitCostAtEvent = product.CostPrice,
                MovementValue = movementValue,
                Notes = string.Join(" | ", new[] { notes, batchNote }.Where(x => !string.IsNullOrWhiteSpace(x))),
                CreatedAt = DateTime.Now,
                CreatedById = userId
            });

            await _context.SaveChangesAsync();

            return new DistributionResult
            {
                Success = true,
                BatchNote = batchNote,
                NewCentralStock = product.StockQuantity,
                NewBranchStock = pb.StockQuantity,
                BranchUnitLabel = pb.StockUnitLabel,
                BranchQuantityAdded = branchQty,
                BaseQuantity = baseQty,
                MovementValue = movementValue
            };
        }

        // ─────────────────────────────────────────────────────────
        // ADJUSTMENTS / WASTE / RETURNS
        // ─────────────────────────────────────────────────────────
        public async Task<(bool ok, string? message)> AdjustAsync(
            int productId, decimal quantity, bool asPackage, InventoryLogType type,
            string reason, int? branchId, string? userId)
        {
            var product = await _context.Products.FindAsync(productId);
            if (product == null) return (false, "المنتج غير موجود");

            var baseQty = ToBaseUnits(product, Math.Abs(quantity), asPackage);
            if (baseQty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر");

            // Waste removes stock; ReturnToWarehouse pulls it back from a branch; Adjustment can go either way
            var isOutbound = type == InventoryLogType.Waste;
            var delta = isOutbound ? -baseQty : (quantity < 0 ? -baseQty : baseQty);

            if (type == InventoryLogType.ReturnToWarehouse)
            {
                if (!branchId.HasValue) return (false, "يجب تحديد الفرع المرتجع منه");
                var pb = await _context.ProductBranches
                    .FirstOrDefaultAsync(x => x.ProductId == productId && x.BranchId == branchId.Value);
                if (pb == null || pb.StockQuantity < baseQty)
                    return (false, "الكمية الموجودة بالفرع أقل من الكمية المرتجعة");
                pb.StockQuantity -= baseQty;
                delta = baseQty; // comes back into the central warehouse
            }
            else if (isOutbound && branchId.HasValue)
            {
                // waste recorded against a branch consumes that branch's stock, not central
                var pb = await _context.ProductBranches
                    .FirstOrDefaultAsync(x => x.ProductId == productId && x.BranchId == branchId.Value);
                if (pb == null || pb.StockQuantity < baseQty)
                    return (false, "الكمية الموجودة بالفرع أقل من الكمية التالفة");
                pb.StockQuantity -= baseQty;

                _context.InventoryLogs.Add(new InventoryLog
                {
                    ProductId = productId,
                    BranchId = branchId,
                    QuantityChange = -baseQty,
                    QuantityBefore = product.StockQuantity,
                    QuantityAfter = product.StockQuantity,
                    Reason = reason,
                    Type = type,
                    EnteredQuantity = Math.Abs(quantity),
                    EnteredAsPackaging = asPackage ? product.PackagingName : null,
                    UnitCostAtEvent = product.CostPrice,
                    MovementValue = Math.Round(product.CostPrice * baseQty, 2),
                    CreatedAt = DateTime.Now,
                    CreatedById = userId
                });
                await _context.SaveChangesAsync();
                return (true, null);
            }

            if (delta < 0 && product.StockQuantity < baseQty)
                return (false, "الكمية المتاحة بالمخزن أقل من الكمية المطلوبة");

            var before = product.StockQuantity;
            product.StockQuantity += delta;

            _context.InventoryLogs.Add(new InventoryLog
            {
                ProductId = productId,
                BranchId = branchId,
                QuantityChange = delta,
                QuantityBefore = before,
                QuantityAfter = product.StockQuantity,
                Reason = reason,
                Type = type,
                EnteredQuantity = Math.Abs(quantity),
                EnteredAsPackaging = asPackage ? product.PackagingName : null,
                UnitCostAtEvent = product.CostPrice,
                MovementValue = Math.Round(product.CostPrice * baseQty, 2),
                CreatedAt = DateTime.Now,
                CreatedById = userId
            });

            await _context.SaveChangesAsync();
            return (true, null);
        }

        // ─────────────────────────────────────────────────────────
        // VALUATION & LEDGER
        // ─────────────────────────────────────────────────────────
        /// <summary>Batches still holding stock, ordered the way they should be used up.</summary>
        public IQueryable<SupplyReceiptItem> OpenBatches() => _context.SupplyReceiptItems
            .Include(i => i.Product)
            .Include(i => i.SupplyReceipt).ThenInclude(r => r!.Supplier)
            .Where(i => i.RemainingQuantity > 0);

        public async Task<(int expired, int expiringSoon)> GetExpiryAlertCountsAsync(int defaultWarnDays = 14)
        {
            var today = DateTime.Today;
            var batches = await OpenBatches().Where(i => i.ExpiryDate != null).ToListAsync();
            var expired = batches.Count(b => b.ExpiryDate!.Value.Date < today);
            var soon = batches.Count(b =>
            {
                var warn = b.Product?.ExpiryWarningDays ?? defaultWarnDays;
                var d = (b.ExpiryDate!.Value.Date - today).TotalDays;
                return d >= 0 && d <= warn;
            });
            return (expired, soon);
        }

        public async Task<decimal> GetOpeningBalanceAsync(int productId, DateTime from)
            => await _context.InventoryLogs
                .Where(l => l.ProductId == productId && l.CreatedAt < from.Date)
                .SumAsync(l => (decimal?)l.QuantityChange) ?? 0;

        public async Task<List<InventoryLog>> GetLedgerAsync(int productId, DateTime from, DateTime to)
            => await _context.InventoryLogs
                .Include(l => l.Branch)
                .Where(l => l.ProductId == productId
                         && l.CreatedAt.Date >= from.Date && l.CreatedAt.Date <= to.Date)
                .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
                .ToListAsync();
    }
}
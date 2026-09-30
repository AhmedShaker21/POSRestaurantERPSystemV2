using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RestaurantERP.Models
{
    // ===================== BRANCH =====================
    public class Branch
    {
        public int Id { get; set; }
        [Required] public string Name { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? ManagerId { get; set; }
        public ApplicationUser? Manager { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsMainBranch { get; set; } = false;
        public string ColorHex { get; set; } = "#2563a8";
        public string? Icon { get; set; } = "🏢";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public ICollection<DiningTable> Tables { get; set; } = new List<DiningTable>();
        public ICollection<Order> Orders { get; set; } = new List<Order>();
        public ICollection<Expense> Expenses { get; set; } = new List<Expense>();
        public ICollection<Shift> Shifts { get; set; } = new List<Shift>();
        public ICollection<UserBranch> UserBranches { get; set; } = new List<UserBranch>();
        public ICollection<ProductBranch> ProductBranches { get; set; } = new List<ProductBranch>();
    }

    public class UserBranch
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public bool IsPrimary { get; set; } = false;
    }

    // NEW: Product <-> Branch many-to-many
    public class ProductBranch
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public Product? Product { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }

        // ── SET WHEN DISTRIBUTING ────────────────────────────────
        // A product is created as bare master data (name, unit, packaging). Everything
        // commercial is decided when the item is actually sent to a branch, because the
        // same item can be priced differently per branch.
        [Column(TypeName = "decimal(18,2)")] public decimal? OverridePrice { get; set; }   // selling price here
        [Column(TypeName = "decimal(5,2)")] public decimal? TaxRateOverride { get; set; }  // null → system rate
        public bool IsAvailable { get; set; } = true;        // show on this branch's cashier grid
        public bool? SkipKitchenOverride { get; set; }        // null → inherit category
        public int? MinStockAlertOverride { get; set; }       // null → product default

        // How much of this product is currently allocated/on-hand at this branch,
        // after being distributed out of the single central warehouse (Product.StockQuantity
        // represents what's still sitting centrally, undistributed).
        [Column(TypeName = "decimal(18,3)")] public decimal StockQuantity { get; set; } = 0;

        // Set when the stock is distributed. true  → the branch sells this directly
        // (e.g. a bottle of water off a carton); false → it's a raw ingredient consumed
        // inside a cooked dish (flour, rice, meat by the kilo) and must not appear on
        // the cashier's product grid as a sellable line.
        public bool IsSellable { get; set; } = true;

        // How many days the current allocation is meant to cover (for ingredients).
        public int? ExpectedDurationDays { get; set; }
        public DateTime? LastDistributedAt { get; set; }

        // StockQuantity above is counted in THIS unit. For a bulk product sold by the
        // portion it holds servings ("كباية"), not the warehouse unit ("كيلوجرام"),
        // because that's what the cashier actually sells and decrements.
        public string? StockUnitLabel { get; set; }
        [Column(TypeName = "decimal(18,3)")] public decimal ServingsPerUnitSnapshot { get; set; } = 1;
    }

    // ===================== CATEGORY =====================
    public class Category
    {
        public int Id { get; set; }
        [Required] public string Name { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; } = "🍽️";
        public string ColorHex { get; set; } = "#FF6B35";
        public bool IsActive { get; set; } = true;
        public bool SkipKitchen { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public ICollection<Product> Products { get; set; } = new List<Product>();
    }

    // ===================== PRODUCT =====================
    public class Product
    {
        public int Id { get; set; }
        [Required] public string Name { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? DescriptionAr { get; set; }

        [Column(TypeName = "decimal(18,2)")] public decimal Price { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal CostPrice { get; set; }

        // Box/Unit pricing
        public bool SellByBox { get; set; } = false;
        public int UnitsPerBox { get; set; } = 1;
        [Column(TypeName = "decimal(18,2)")] public decimal BoxCostPrice { get; set; } = 0;
        [Column(TypeName = "decimal(18,2)")] public decimal BoxSellPrice { get; set; } = 0;
        public string? BoxBarcode { get; set; }

        // Per-product tax override (null = use system default 14%)
        [Column(TypeName = "decimal(5,2)")] public decimal? TaxRateOverride { get; set; }

        public int CategoryId { get; set; }
        public Category? Category { get; set; }

        // Overrides the category's SkipKitchen default for this one product.
        // null = inherit the category setting; true/false = force it for this product
        // (e.g. a "Drinks" category might mostly need prep, but bottled water/canned
        // soda in it should still fire straight out without going to the kitchen).
        public bool? SkipKitchenOverride { get; set; }
        public string? ImageUrl { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsAvailable { get; set; } = true;
        [Column(TypeName = "decimal(18,3)")] public decimal StockQuantity { get; set; } = 0;
        public int MinStockAlert { get; set; } = 5;
        public bool TrackStock { get; set; } = false;

        // Unit of measure the StockQuantity is expressed in (كيلوجرام / جرام / زجاجة / قطعة / لتر ...)
        // This is the BASE stock-keeping unit — everything in the warehouse is stored in this unit.
        public string Unit { get; set; } = "قطعة";

        // ── INVENTORY PACKAGING ──────────────────────────────────
        // Optional outer packaging used when receiving/distributing stock.
        // e.g. Unit = "زجاجة", PackagingName = "كرتونة", UnitsPerPackage = 12
        //      → receiving 30 كرتونة adds 360 زجاجة to the warehouse.
        public string? PackagingName { get; set; }
        [Column(TypeName = "decimal(18,3)")] public decimal UnitsPerPackage { get; set; } = 1;
        [NotMapped] public bool HasPackaging => !string.IsNullOrWhiteSpace(PackagingName) && UnitsPerPackage > 1;

        // Default shelf life in days. When receiving, the expiry date is pre-filled as
        // (receipt date + this), so the storekeeper doesn't retype it every time.
        public int? ShelfLifeDays { get; set; }
        // Warn this many days before expiry (falls back to a system-wide default).
        public int? ExpiryWarningDays { get; set; }

        // ── SERVING YIELD (optional) ─────────────────────────────
        // For bulk goods that are sold by the portion rather than by weight:
        // Unit = "كيلوجرام", ServingName = "كباية", ServingsPerUnit = 10
        //   → distributing 2 kg puts 20 كباية on the branch's shelf, and that's
        //     what the cashier sells and decrements.
        // Leave ServingName empty and the product is distributed in its base unit.
        public string? ServingName { get; set; }
        [Column(TypeName = "decimal(18,3)")] public decimal ServingsPerUnit { get; set; } = 1;
        [NotMapped] public bool HasServings => !string.IsNullOrWhiteSpace(ServingName) && ServingsPerUnit > 1;

        public string Barcode { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public ICollection<ProductBranch> ProductBranches { get; set; } = new List<ProductBranch>();

        [NotMapped] public decimal EffectivePrice => SellByBox && UnitsPerBox > 0 ? BoxSellPrice / UnitsPerBox : Price;
        [NotMapped] public decimal EffectiveCost => SellByBox && UnitsPerBox > 0 ? BoxCostPrice / UnitsPerBox : CostPrice;
        [NotMapped] public decimal EffectiveTaxRate => TaxRateOverride ?? 14m;
    }

    // ===================== TABLE =====================
    public class DiningTable
    {
        public int Id { get; set; }
        [Required] public string TableNumber { get; set; } = string.Empty;
        public int Capacity { get; set; } = 4;
        public TableStatus Status { get; set; } = TableStatus.Available;
        public string? Section { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public ICollection<Order> Orders { get; set; } = new List<Order>();
    }

    public enum TableStatus { Available, Occupied, Reserved, Cleaning, Maintenance }

    // ===================== ORDER =====================
    public class Order
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Pending;
        public OrderType OrderType { get; set; } = OrderType.DineIn;
        public int? TableId { get; set; }
        public DiningTable? Table { get; set; }
        public string? CashierId { get; set; }
        public ApplicationUser? Cashier { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Notes { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal SubTotal { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal TaxRate { get; set; } = 14;
        [Column(TypeName = "decimal(18,2)")] public decimal TaxAmount { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal DiscountAmount { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal Total { get; set; }
        public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
        [Column(TypeName = "decimal(18,2)")] public decimal AmountPaid { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal Change { get; set; }

        // ── DEFERRED PAYMENT (open tab) ──────────────────────────
        // Dine-in customers often keep ordering before settling. An order placed on an
        // open tab is created with IsPaid = false and is only collected when the cashier
        // closes the table, at which point every unpaid order on that table is settled
        // together into one combined bill.
        public bool IsPaid { get; set; } = true;
        public DateTime? PaidAt { get; set; }

        public bool IsPrinted { get; set; } = false;
        public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    }

    public enum OrderStatus { Pending, Preparing, Ready, Completed, Cancelled, Refunded, PartialRefund }
    public enum OrderType { DineIn, Takeaway, Delivery }
    public enum PaymentMethod { Cash, Card, Digital }

    // ===================== ORDER ITEM =====================
    public class OrderItem
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order? Order { get; set; }
        public int ProductId { get; set; }
        public Product? Product { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string ProductNameAr { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        [Column(TypeName = "decimal(18,2)")] public decimal UnitPrice { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal TotalPrice { get; set; }

        // Cost of the goods at the moment of sale. Snapshotted (not read live from
        // Product.CostPrice) so that changing a product's cost later doesn't silently
        // rewrite the profit on every past order.
        [Column(TypeName = "decimal(18,2)")] public decimal UnitCost { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal TotalCost { get; set; }

        public string? Notes { get; set; }
        public bool SkipKitchen { get; set; } = false;
    }

    // ===================== REFUND =====================
    public class Refund
    {
        public int Id { get; set; }
        public string RefundNumber { get; set; } = string.Empty;
        public int OriginalOrderId { get; set; }
        public Order? OriginalOrder { get; set; }
        public string? ProcessedById { get; set; }
        public ApplicationUser? ProcessedBy { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public RefundType RefundType { get; set; } = RefundType.Full;
        public RefundMethod RefundMethod { get; set; } = RefundMethod.Cash;
        [Column(TypeName = "decimal(18,2)")] public decimal RefundAmount { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal RefundTax { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal RefundTotal { get; set; }
        public string? Reason { get; set; }
        public string? Notes { get; set; }
        public RefundStatus Status { get; set; } = RefundStatus.Completed;
        public ICollection<RefundItem> Items { get; set; } = new List<RefundItem>();
    }

    public class RefundItem
    {
        public int Id { get; set; }
        public int RefundId { get; set; }
        public Refund? Refund { get; set; }
        public int OrderItemId { get; set; }
        public OrderItem? OrderItem { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string ProductNameAr { get; set; } = string.Empty;
        public int Quantity { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal UnitPrice { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal TotalPrice { get; set; }
    }

    public enum RefundType { Full, Partial }
    public enum RefundMethod { Cash, Card, Digital, StoreCredit }
    public enum RefundStatus { Pending, Completed, Rejected }

    // ===================== EXPENSE =====================
    public class Expense
    {
        public int Id { get; set; }
        [Required] public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
        public string Category { get; set; } = string.Empty;
        public string? PaymentMethod { get; set; }
        public string? Notes { get; set; }
        public DateTime Date { get; set; } = DateTime.Now;
        public string? CreatedById { get; set; }
        public ApplicationUser? CreatedBy { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public ApplicationUser? RecordedBy { get; set; }
        public string? RecordedById { get; set; }
    }

    // ═══════════════════════════════════════════════════════════
    //                         INVENTORY
    // ═══════════════════════════════════════════════════════════

    // ── SUPPLIER ────────────────────────────────────────────────
    public class Supplier
    {
        public int Id { get; set; }
        [Required] public string Name { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public ICollection<SupplyReceipt> Receipts { get; set; } = new List<SupplyReceipt>();
    }

    // ── SUPPLY RECEIPT (ايصال توريد) ────────────────────────────
    // A real goods-received note: one receipt, one supplier, many product lines.
    public class SupplyReceipt
    {
        public int Id { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTime Date { get; set; } = DateTime.Now;

        public int? SupplierId { get; set; }
        public Supplier? Supplier { get; set; }
        public string? SupplierNameSnapshot { get; set; }

        [Column(TypeName = "decimal(18,2)")] public decimal TotalCost { get; set; }

        // Supplier settlement tracking — lets the accountant see outstanding payables
        public bool IsPaid { get; set; } = true;
        [Column(TypeName = "decimal(18,2)")] public decimal AmountPaid { get; set; }
        public DateTime? PaidAt { get; set; }

        public string? InvoiceNumber { get; set; }   // supplier's own invoice ref
        public string? Notes { get; set; }
        public string? CreatedById { get; set; }
        public ApplicationUser? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public ICollection<SupplyReceiptItem> Items { get; set; } = new List<SupplyReceiptItem>();

        [NotMapped] public decimal Outstanding => TotalCost - AmountPaid;
    }

    public class SupplyReceiptItem
    {
        public int Id { get; set; }
        public int SupplyReceiptId { get; set; }
        public SupplyReceipt? SupplyReceipt { get; set; }
        public int ProductId { get; set; }
        public Product? Product { get; set; }

        public string ProductNameSnapshot { get; set; } = string.Empty;
        public string UnitSnapshot { get; set; } = string.Empty;

        // What the user actually typed (e.g. 30 كرتونة) …
        [Column(TypeName = "decimal(18,3)")] public decimal EnteredQuantity { get; set; }
        public string? EnteredAsPackaging { get; set; }              // null = entered in base units
        [Column(TypeName = "decimal(18,3)")] public decimal UnitsPerPackageSnapshot { get; set; } = 1;

        // … converted into base stock units (e.g. 360 زجاجة)
        [Column(TypeName = "decimal(18,3)")] public decimal BaseQuantity { get; set; }

        // Cost is captured per whatever unit the user entered, then normalised
        [Column(TypeName = "decimal(18,2)")] public decimal CostPerEnteredUnit { get; set; }
        [Column(TypeName = "decimal(18,4)")] public decimal CostPerBaseUnit { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal LineTotal { get; set; }

        // ── BATCH / EXPIRY ──────────────────────────────────────
        // Every receipt line is effectively a batch. Tracking what's left of each one
        // lets the warehouse consume oldest-expiry-first (FIFO) and warn before stock
        // goes off.
        public DateTime? ExpiryDate { get; set; }
        public DateTime? ProductionDate { get; set; }
        public string? BatchNumber { get; set; }

        /// <summary>Base units still un-distributed from this specific batch.</summary>
        [Column(TypeName = "decimal(18,3)")] public decimal RemainingQuantity { get; set; }

        [NotMapped] public bool IsExpired => ExpiryDate.HasValue && ExpiryDate.Value.Date < DateTime.Today;
        [NotMapped] public int? DaysToExpiry => ExpiryDate.HasValue
            ? (int)(ExpiryDate.Value.Date - DateTime.Today).TotalDays
            : (int?)null;
    }

    // ── LEDGER ──────────────────────────────────────────────────
    public class InventoryLog
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public Product? Product { get; set; }

        // Central-warehouse quantity delta in BASE units (+ in, − out)
        [Column(TypeName = "decimal(18,3)")] public decimal QuantityChange { get; set; }
        [Column(TypeName = "decimal(18,3)")] public decimal QuantityBefore { get; set; }
        [Column(TypeName = "decimal(18,3)")] public decimal QuantityAfter { get; set; }

        public string Reason { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string? CreatedById { get; set; }

        public InventoryLogType Type { get; set; } = InventoryLogType.Adjustment;

        // Set for Distribution entries: which branch received the stock
        public int? BranchId { get; set; }
        public Branch? Branch { get; set; }

        // Link back to the receipt this line came from (Supply entries)
        public int? SupplyReceiptId { get; set; }
        public SupplyReceipt? SupplyReceipt { get; set; }
        public string? ReceiptNumber { get; set; }

        // How the user originally expressed this movement, for readable reports
        // (e.g. "30 كرتونة" even though the ledger stores 360 زجاجة)
        [Column(TypeName = "decimal(18,3)")] public decimal? EnteredQuantity { get; set; }
        public string? EnteredAsPackaging { get; set; }

        [Column(TypeName = "decimal(18,2)")] public decimal? UnitCost { get; set; }
        public string? SupplierName { get; set; }
        public string? Notes { get; set; }

        // For Distribution entries: how long this batch is expected to last at the branch,
        // plus a cost snapshot — used to amortize cost across days in the profit report.
        public int? ExpectedDurationDays { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal? UnitCostAtEvent { get; set; }

        // For Distribution entries: true = branch resells it as-is, false = raw ingredient
        public bool? DistributedAsSellable { get; set; }

        // Value of this movement (base qty × cost) — lets the accountant total money in/out
        [Column(TypeName = "decimal(18,2)")] public decimal? MovementValue { get; set; }
    }

    public enum InventoryLogType { Supply, Distribution, NewProduct, Sale, Refund, Adjustment, Waste, ReturnToWarehouse }

    // ===================== SETTINGS =====================
    public class SystemSettings
    {
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public int? BranchId { get; set; }
        public Branch? Branch { get; set; }
    }

    // ===================== SHIFT =====================
    public class Shift
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }
        public int BranchId { get; set; }
        public Branch? Branch { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime? EndTime { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal OpeningCash { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal ClosingCash { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal TotalSales { get; set; }
        public int TotalOrders { get; set; }
        public bool IsClosed { get; set; } = false;
        public string? Notes { get; set; }
        public bool IsActive => !IsClosed;
        public ApplicationUser? Cashier => User;
        public ICollection<Order> Orders { get; set; } = new List<Order>();
    }
}

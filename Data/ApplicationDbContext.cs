using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Models;
using System.Reflection.Emit;

namespace RestaurantERP.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<Branch> Branches { get; set; }
        public DbSet<UserBranch> UserBranches { get; set; }
        public DbSet<ProductBranch> ProductBranches { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<DiningTable> DiningTables { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public DbSet<Expense> Expenses { get; set; }
        public DbSet<InventoryLog> InventoryLogs { get; set; }
        public DbSet<Supplier> Suppliers { get; set; }
        public DbSet<SupplyReceipt> SupplyReceipts { get; set; }
        public DbSet<SupplyReceiptItem> SupplyReceiptItems { get; set; }
        public DbSet<SystemSettings> SystemSettings { get; set; }
        public DbSet<Shift> Shifts { get; set; }
        public DbSet<Refund> Refunds { get; set; }
        public DbSet<RefundItem> RefundItems { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Branch>()
                .HasOne(b => b.Manager).WithMany()
                .HasForeignKey(b => b.ManagerId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<UserBranch>()
                .HasOne(ub => ub.User).WithMany(u => u.UserBranches)
                .HasForeignKey(ub => ub.UserId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<UserBranch>()
                .HasOne(ub => ub.Branch).WithMany(b => b.UserBranches)
                .HasForeignKey(ub => ub.BranchId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ApplicationUser>()
                .HasOne(u => u.DefaultBranch).WithMany()
                .HasForeignKey(u => u.DefaultBranchId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<DiningTable>()
                .HasOne(t => t.Branch).WithMany(b => b.Tables)
                .HasForeignKey(t => t.BranchId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Order>()
                .HasOne(o => o.Table).WithMany(t => t.Orders)
                .HasForeignKey(o => o.TableId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<Order>()
                .HasOne(o => o.Cashier).WithMany()
                .HasForeignKey(o => o.CashierId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<Order>()
                .HasOne(o => o.Branch).WithMany(b => b.Orders)
                .HasForeignKey(o => o.BranchId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<OrderItem>()
                .HasOne(oi => oi.Product).WithMany(p => p.OrderItems)
                .HasForeignKey(oi => oi.ProductId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Expense>()
                .HasOne(e => e.CreatedBy).WithMany()
                .HasForeignKey(e => e.CreatedById).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<Expense>()
                .HasOne(e => e.Branch).WithMany(b => b.Expenses)
                .HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<InventoryLog>()
                .HasOne(il => il.Product).WithMany()
                .HasForeignKey(il => il.ProductId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<InventoryLog>()
                .HasOne(il => il.Branch).WithMany()
                .HasForeignKey(il => il.BranchId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<InventoryLog>()
                .HasOne(il => il.SupplyReceipt).WithMany()
                .HasForeignKey(il => il.SupplyReceiptId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<SupplyReceipt>()
                .HasOne(r => r.Supplier).WithMany(s => s.Receipts)
                .HasForeignKey(r => r.SupplierId).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<SupplyReceipt>()
                .HasOne(r => r.CreatedBy).WithMany()
                .HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<SupplyReceipt>()
                .HasIndex(r => r.ReceiptNumber).IsUnique();

            builder.Entity<SupplyReceiptItem>()
                .HasOne(i => i.SupplyReceipt).WithMany(r => r.Items)
                .HasForeignKey(i => i.SupplyReceiptId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<SupplyReceiptItem>()
                .HasOne(i => i.Product).WithMany()
                .HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Shift>()
                .HasOne(s => s.User).WithMany()
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<Shift>()
                .HasOne(s => s.Branch).WithMany(b => b.Shifts)
                .HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Refund>()
                .HasOne(r => r.OriginalOrder).WithMany()
                .HasForeignKey(r => r.OriginalOrderId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Refund>()
                .HasOne(r => r.ProcessedBy).WithMany()
                .HasForeignKey(r => r.ProcessedById).OnDelete(DeleteBehavior.SetNull);

            builder.Entity<Refund>()
                .HasOne(r => r.Branch).WithMany()
                .HasForeignKey(r => r.BranchId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<RefundItem>()
                .HasOne(ri => ri.Refund).WithMany(r => r.Items)
                .HasForeignKey(ri => ri.RefundId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<RefundItem>()
                .HasOne(ri => ri.OrderItem).WithMany()
                .HasForeignKey(ri => ri.OrderItemId).OnDelete(DeleteBehavior.Restrict);

            builder.Entity<SystemSettings>()
                .HasOne(s => s.Branch).WithMany()
                .HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Cascade);

            // ProductBranch many-to-many
            builder.Entity<ProductBranch>()
                .HasOne(pb => pb.Product).WithMany(p => p.ProductBranches)
                .HasForeignKey(pb => pb.ProductId).OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ProductBranch>()
                .HasOne(pb => pb.Branch).WithMany(b => b.ProductBranches)
                .HasForeignKey(pb => pb.BranchId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
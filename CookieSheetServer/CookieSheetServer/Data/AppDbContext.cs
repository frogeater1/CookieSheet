using CookieSheetServer.Data.Entities;
using CookieSheet.Refunds;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<user> user { get; set; }
    public DbSet<order> order { get; set; }
    public DbSet<RefundRecord> refunds { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RefundRecord>().HasKey(r => r.refundId);
        modelBuilder.Entity<RefundRecord>().HasIndex(r => r.orderId);
        modelBuilder.Entity<RefundRecord>().ToTable("refunds", table =>
            table.HasCheckConstraint("CK_refunds_successful_status", "\"status\" = '退款成功'"));
        modelBuilder.Entity<order>().Property(o => o.productUnitPrice).HasDefaultValueSql("ARRAY[]::numeric[]");
        modelBuilder.Entity<order>().Property(o => o.productId).HasDefaultValueSql("ARRAY[]::text[]");

        // Configure OrderId to be the key
        modelBuilder.Entity<order>()
            .HasKey(o => o.orderId);

        // Configure relationships
        modelBuilder.Entity<order>()
            .HasOne(o => o.user)
            .WithMany(u => u.Orders)
            .HasForeignKey(o => o.userId);

        // PostgreSql specific configurations for arrays if needed
        // Npgsql supports string[] out of the box for Postgres text[]

        // 定义一个转换器：写入数据库前转 UTC，读取出来时不做处理
        var offsetConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
            v => v.ToUniversalTime(),
            v => v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(offsetConverter);
                }
            }
        }
    }
}

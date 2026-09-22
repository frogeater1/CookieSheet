using CookieSheet.Refunds;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using CookieSheetServer.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CookieSheet.Refunds.Tests;

public class RefundDeletionTests
{
    [Fact]
    public async Task DeletesOnlySelectedRefundAndPersistsWithoutChangingOrdersOrUsers()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var db = new AppDbContext(options))
        {
            db.user.Add(new user { userId = 1, tel = "", qq = "", cn = "测试", receiver = "", account = "", address = "" });
            db.order.Add(new order { orderId = "o1", userId = 1, tel = "", tag = "测试", productName = ["原商品"],
                productModel = ["型号"], productCount = [5], productUnitPrice = [7], price = 35 });
            db.refunds.AddRange(new RefundRecord { refundId = "delete-me", orderId = "o1", productName = "未匹配商品", status = "退款成功" },
                new RefundRecord { refundId = "keep-me", orderId = "o1", productName = "原商品", status = "退款成功" });
            await db.SaveChangesAsync();
            await RefundImportService.DeleteAsync(db, "delete-me");
        }
        using (var db = new AppDbContext(options))
        {
            Assert.Equal("keep-me", (await db.refunds.SingleAsync()).refundId);
            Assert.Equal(5, (await db.order.SingleAsync()).productCount[0]);
            Assert.Equal(35, (await db.order.SingleAsync()).price);
            Assert.Single(await db.user.ToListAsync());
            await RefundImportService.DeleteAsync(db, "delete-me");
            await RefundImportService.DeleteAsync(db, "missing");
            Assert.Single(await db.refunds.ToListAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => RefundImportService.DeleteAsync(db, " "));
            Assert.Single(await db.refunds.ToListAsync());
        }
    }
}

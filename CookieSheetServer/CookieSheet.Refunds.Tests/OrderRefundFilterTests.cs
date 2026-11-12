using CookieSheet.Data;
using CookieSheet.Helpers;
using Xunit;

// The linked client entity only uses this WinUI enum for its postage button.
// Filtering tests exercise the actual client entity, view model and query without a UI runtime.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace CookieSheet.Refunds.Tests
{
    public class OrderRefundFilterTests
    {
        private static order Order(string id, decimal expectedRefund) => new()
        {
            orderId = id, tag = "批次A", productName = ["商品"], productModel = ["型号"], productCount = [3],
            price = 21 - expectedRefund, productRefundAmount = expectedRefund, orderedAt = DateTimeOffset.UtcNow
        };

        [Fact]
        public void InvalidRefundsStaySearchableAfterFallbackAndLeaveFilterWhenCorrected()
        {
            var invalid = Order("invalid-order", 0);
            RefundRecord refund = new() { refundId = "r1", orderId = invalid.orderId, productName = "商品",
                productModel = "型号", refundAmount = 7, productPaidAmount = 21, status = "退款成功" };
            invalid.ApplyRefunds([refund]);
            var healthy = Order("healthy", 0);
            healthy.ApplyRefunds([]);
            var awaiting = Order("awaiting", 7);
            awaiting.ApplyRefunds([]);
            OrderListItemViewModel[] items = [new(invalid), new(healthy), new(awaiting)];
            var found = Assert.Single(OrderListQuery.Apply(items, keyword: "invalid", tag: "批次A",
                refundFilter: OrderRefundFilter.InvalidDetails));
            Assert.Equal(invalid.orderId, found.OrderId);
            Assert.Equal(0, Assert.Single(found.ProductRefunds).RefundedQuantity);
            Assert.Equal(3, SoldProductsQuery.Apply([new(found.OrderId, found.Tag, found.ProductRefunds)]).NetQuantity);
            Assert.Contains(OrderListQuery.Apply(items, refundFilter: OrderRefundFilter.AnyRefund), item => item.OrderId == invalid.orderId);
            Assert.Equal("healthy", Assert.Single(OrderListQuery.Apply(items, refundFilter: OrderRefundFilter.None)).OrderId);
            Assert.Equal("awaiting", Assert.Single(OrderListQuery.Apply(items, refundFilter: OrderRefundFilter.AwaitingDetails)).OrderId);
            invalid.productRefundAmount = 7;
            invalid.ApplyRefunds([refund]);
            Assert.Empty(OrderListQuery.Apply([new(invalid)], refundFilter: OrderRefundFilter.InvalidDetails));
            Assert.Equal(1, Assert.Single(invalid.ProductRefunds).RefundedQuantity);
        }
    }
}

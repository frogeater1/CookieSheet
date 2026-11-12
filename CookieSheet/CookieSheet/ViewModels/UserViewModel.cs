using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using CookieSheet.Data;

namespace CookieSheet
{
    public class UserViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyPostageChanged() => PropertyChanged?.Invoke(this, new(nameof(HeaderOpacity)));
        public void ReplaceOrders(List<order> orders)
        {
            Orders = orders;
            foreach (var name in new[] { nameof(Orders), nameof(TotalAmount), nameof(OrderCountText), nameof(HeaderOpacity) })
                PropertyChanged?.Invoke(this, new(name));
        }
        public user User { get; set; }
        public List<order> Orders { get; set; }
        public decimal TotalAmount => Orders?.Sum(o => o.price) ?? 0;

        // Helper properties for display
        public string Title => $"{User.cn} ({User.qq})";
        public string Subtitle => $"收件人: {User.receiver} {User.tel} | {User.address}";
        public string OrderCountText => $"{Orders?.Count ?? 0} 单";

        public double HeaderOpacity => Orders.All(o => o.sended) ? 0.5 : 1.0;
    }
}

using System.Collections.Generic;
using System.Linq;
using CookieSheet.Data;

namespace CookieSheet
{
    public class UserViewModel
    {
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
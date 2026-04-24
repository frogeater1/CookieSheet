using System.Collections.Generic;
namespace CookieSheet.Data;

public class user
{
    public int userId { get; set; }

    public string tel { get; set; }
    public string qq { get; set; }

    public string cn { get; set; }

    public string receiver { get; set; }

    public string account { get; set; }

    public decimal price { get; set; }

    public string address { get; set; }

    public List<order> orders { get; set; } = [];
}

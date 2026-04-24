using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace CookieSheetServer.Data.Entities;

public class user
{
    [Key] public int userId { get; set; }

    public string tel { get; set; }
    public string qq { get; set; }

    public string cn { get; set; }

    public string receiver { get; set; }

    public string account { get; set; }

    public decimal price { get; set; }

    public string address { get; set; }

    [JsonIgnore]
    [InverseProperty("User")] public List<order> Orders { get; set; } = [];
}

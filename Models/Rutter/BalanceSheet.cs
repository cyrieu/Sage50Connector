using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Models.Rutter
{
    //BalanceSheet myDeserializedClass = JsonConvert.DeserializeObject<BalanceSheet>(myJsonResponse);
    public class Test
    {
        public string ad { get; set; }  
    }
    public class Assets
    {
        public string account_id { get; set; }
        public string name { get; set; }
        public decimal value { get; set; }
        public List<Item> items { get; set; }
    }

    public class Equity
    {
        public string account_id { get; set; }
        public string name { get; set; }
        public decimal value { get; set; }
        public List<Item> items { get; set; }
    }
    public class AccountBalance
    {
        public string AccountID { get; set; }
        public string Description { get; set; }
        public decimal Balance { get; set; }
    }

    public class Item
    {
        public string account_id { get; set; }
        public string name { get; set; }
        public decimal value { get; set; }
        public List<Item> items { get; set; }
    }

    public class Liabilities
    {
        public string account_id { get; set; }
        public string name { get; set; }
        public decimal value { get; set; }
        public List<Item> items { get; set; }
    }

    public class PlatformData
    {
        public int id { get; set; }
        public string data { get; set; }
    }

    public class BalanceSheet
    {
        public string id { get; set; }
        public string start_date { get; set; }
        public string end_date { get; set; }
        public string currency_code { get; set; }
        public decimal total_assets { get; set; }
        public decimal total_equity { get; set; }
        public decimal total_liabilities { get; set; }
        public Assets assets { get; set; }
        public Equity equity { get; set; }
        public Liabilities liabilities { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public PlatformData platform_data { get; set; }
    }

}

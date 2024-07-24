using Sage.Peachtree.API;
using System;
using System.Runtime.InteropServices;

namespace Sage50Connector.Models.Rutter
{
    public class ChartofAccount
    {
        public string Classification { get; set; }
        public string Description { get; set; }
        public string ID { get; set; }
        public bool IsAdded { get; set; }
        public bool IsDeleteAllowed { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsInactive { get; set; }
        public bool IsModified { get; set; }
        public bool IsSaveAllowed { get; set; }
        public bool IsUnchanged { get; set; }
        public EntityReference<Account> Key { get; set; }
        public int Revision { get; set; }
    }
}

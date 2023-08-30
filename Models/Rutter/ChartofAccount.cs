using Sage.Peachtree.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Models.Rutter
{
    public class ChartofAccount
    {
        public string ID { get; set; }
        public string Description { get; set; }
        public string Classification { get; set; }
        public bool IsInactive { get; set; }
    }
}

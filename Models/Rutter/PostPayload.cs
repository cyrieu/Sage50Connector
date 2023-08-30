using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Sage50Connector.Models.Rutter
{
    public class PostPayload<T>  where T : class 
    {
        public Connection Connection { get; set; }        
        public string Entity { get; set; }
        public PayloadContainer<T> Payload { get; set; }
    }
    public class PayloadContainer<T> where T : class
    {
        public List<T> Data { get; set; }
    }
}

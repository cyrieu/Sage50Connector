using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Helpers
{
    static class GlobalFunctions
    {
        public static object loadValue(object currentObject, string[] pathElements)
        {

            foreach (var element in pathElements)
            {
                if (currentObject == null)
                {
                    return null;
                }
                var tempMethod = currentObject.GetType().GetMethod(element);
                if (tempMethod != null)
                {
                    currentObject = tempMethod.Invoke(currentObject, null);
                    continue;
                }

                var property = currentObject.GetType().GetProperty(element);
                if (property == null)
                {
                    return null;
                }

                currentObject = property.GetValue(currentObject);
                if (currentObject == null)
                {
                    return null;
                }
            }

            return currentObject;
        }
    }
}

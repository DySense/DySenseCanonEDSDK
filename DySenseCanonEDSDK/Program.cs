using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DySenseCanonEDSDK
{
    public class Program
    {
        public static void Main(string[] args)
        {
           
            //string sensorID = "cam";
            //string instrumentID = "123";
            //Dictionary<string, object> settings = new Dictionary<string, object> { { "out_directory", @"C:\Users\Poland PheMU\Documents\canon_test_output" }, { "trigger_period", "4" } };
            //string connectEndpoint = "tcp://127.0.0.1:60000";

            string sensorID = args[0];
            string instrumentID = args[1];
            Dictionary<string, object> settings = JsonConvert.DeserializeObject<Dictionary<string, object>>(args[2]);
            string connectEndpoint = args[3];

            CanonEDSDKDriver driver = new CanonEDSDKDriver(sensorID, instrumentID, settings, connectEndpoint);

            driver.Run();
        }
        
    }
}

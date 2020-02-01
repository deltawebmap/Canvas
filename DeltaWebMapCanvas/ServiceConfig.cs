using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas
{
    public class ServiceConfig
    {
        public string database_config = @"C:\Users\Roman\Documents\delta_dev\backend\database_config.json";
        public int port = 43282;
        public int buffer_size = 512;
        public int timeout_seconds = 8;
        public string[] user_colors = new string[]
        {
            "#F69D26",
            "#F85555",
            "#7DFE61",
            "#25FFC0",
            "#25D1FF",
            "#C853FF",
            "#FF53C0"
        };
        public string map_directory = @"C:\Users\Roman\Documents\delta_dev\backend\canvas\saved\";
    }
}

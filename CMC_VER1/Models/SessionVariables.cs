using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_VER1.Models
{
    public static class SessionVariables
    {
        public static string ApiKey { get; set; }
        public static string Warehouse { get; set; }
        public static string WarehouseCode { get; set; }
        public static int WarehouseID { get; set; }
        public static string UserEmail { get; set; }
    }
    public class WaveList
    {
        public int waveID { get; set; }
        public string waveNumber { get; set; }
    }
}

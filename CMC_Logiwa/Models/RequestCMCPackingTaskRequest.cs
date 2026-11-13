using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_Logiwa.Models
{
    internal class RequestCMCPackingTaskRequest
    {
        public string warehouseCode { get; set; }
        public string waveNumber { get; set; }
        public bool firstCall { get; set; }
    }
}

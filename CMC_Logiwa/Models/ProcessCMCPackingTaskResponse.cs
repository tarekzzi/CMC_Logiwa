using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_Logiwa.Models
{
    internal class ProcessCMCPackingTaskResponse
    {
        public string message { get; set; }
        public List<ProcessCMCPackingTaskItem> data { get; set; }
    }

    internal class ProcessCMCPackingTaskItem
    {
        public string waveNumber { get; set; }
        public string sourceOrderNumber { get; set; }
        public string tenantCode { get; set; }
        public string tenantName { get; set; }
        public string pickingSlipNumber { get; set; }
        public string itemCode { get; set; }
        public string itemBarcode { get; set; }
        public string itemSerialNo { get; set; }
        public int qty { get; set; }
        public string labelDataLen { get; set; }
        public string labelData { get; set; }
        public string matchLab { get; set; }
        public string trackingNumber { get; set; }
    }
}

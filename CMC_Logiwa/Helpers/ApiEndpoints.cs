using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_Logiwa.Helpers
{
    internal class ApiEndpoints
    {
        //public const string GetListAllWavesByTypeAndStatuses =
        //    "https://qaapi.3plwms.com.au/WaveAPI/api/Wave/GetListAllWavesByTypeAndStatuses";

        //public const string RequestCMCPackingTask =
        //    "https://qaapi.3plwms.com.au/WaveAPI/api/Wave/RequestCMCPackingTask";

        //public const string ProcessCMCPackingTask =
        //    "https://qaapi.3plwms.com.au/WaveAPI/api/Wave/ProcessCMCPackingTask";

        //public const string UpdateShipmentInfo =
        //    "https://qaapi.3plwms.com.au/WaveAPI/api/Wave/UpdateShipmentInfo";
        public const string GetListAllWavesByTypeAndStatuses =
            "https://api1.3plwms.com.au/WaveAPI/api/Wave/GetListAllWavesByTypeAndStatuses";

        public const string RequestCMCPackingTask =
            "https://api1.3plwms.com.au/WaveAPI/api/Wave/RequestCMCPackingTask";

        public const string ProcessCMCPackingTask =
            "https://api1.3plwms.com.au/WaveAPI/api/Wave/ProcessCMCPackingTask";

        public const string UpdateShipmentInfo =
            "https://api1.3plwms.com.au/WaveAPI/api/Wave/UpdateShipmentInfo";
    }
}

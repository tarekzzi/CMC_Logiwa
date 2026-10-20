using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_VER1.Models
{
    internal class UpdateShipmentInfoRequest
    {
        public string FulfilmentNumber { get; set; }
        public string WaveNumber { get; set; }
        public string OrderStatus { get; set; }
        public string BoxSizeH { get; set; }
        public string BoxSizeL { get; set; }
        public string BoxSizeW { get; set; }
        public string BoxArea { get; set; }
        public string Weight_Carton { get; set; }
        public string Box_Weight { get; set; }
    }
}

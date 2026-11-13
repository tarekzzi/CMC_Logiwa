using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_Logiwa.Models
{
    public class MessageProcessingResult
    {
        public string RawMessage { get; set; }

        public string MachineId { get; set; }
        public string MessageType { get; set; }

        public bool IsRequest { get; set; }
        public bool IsResponse { get; set; }
        public string Counter { get; set; }  
        public string Barcode { get; set; }   
        public string Source { get; set; }
        public string Reference { get; set; }
        public int AckResult { get; set; }
        public int PrnResult { get; set; }
        public int LabStatus { get; set; }      
        public string LabEvent { get; set; }    
        public int LabSizeH { get; set; }
        public int LabSizeL { get; set; }
        public int LabSizeW { get; set; }
        public int LabArea { get; set; }
        public int LabWeightCarton { get; set; }
        public int LabWeightScale { get; set; }
        public int EndStatus { get; set; }
        public string EndEvent { get; set; }
        public int EndSizeH { get; set; }
        public int EndSizeL { get; set; }
        public int EndSizeW { get; set; }
        public int EndArea { get; set; }
        public int EndWeightCarton { get; set; }
        public int EndWeightScale { get; set; }

    }
}

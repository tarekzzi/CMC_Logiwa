//using CMC_Logiwa.Models;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace CMC_Logiwa.Services
//{
//    public  class FormatProcessMessages
//    {
//        private const char STX = '\u0002';
//        private const char ETX = '\u0003';

//        public MessageProcessingResult ProcessIncomingMessage(string rawMessage)
//        {
//            try
//            {
//                var result = new MessageProcessingResult
//                {
//                    RawMessage = rawMessage
//                };

//                // Example received (without STX/ETX already removed by NetworkManager):
//                // 0001|HBT
//                var parts = rawMessage.Split('|');

//                if (parts.Length < 2)
//                    return null;

//                result.MachineId = parts[0];
//                result.MessageType = parts[1];

//                if (result.MessageType == result.MessageType.ToUpper())
//                    result.IsRequest = true;
//                else
//                    result.IsResponse = true;

//                return result;
//            }
//            catch
//            {
//                return null;
//            }
//        }

//        public string FormatHeartbeatResponse(string machineId)
//        {
//            // <STX>0001|hbt<ETX>
//            return $"{machineId}|hbt";
//        }
//    }
//}
using CMC_Logiwa.Models;
using System;
using System.Text;

namespace CMC_Logiwa
{
    public class FormatProcessMessages
    {
        public MessageProcessingResult ProcessIncomingMessage(string rawMessage)
        {
            rawMessage = (rawMessage ?? string.Empty).Trim();
            var parts = rawMessage.Split('|');

            if (parts.Length < 2)
                return null;

            var result = new MessageProcessingResult
            {
                RawMessage = rawMessage,
                MachineId = parts[0].Trim(),
                MessageType = parts[1].Trim(),
                IsRequest = parts[1] == parts[1].ToUpperInvariant(),
                IsResponse = parts[1] == parts[1].ToLowerInvariant()
            };

            switch (result.MessageType)
            {
                case "HBT":
                case "hbt":
                    break;

                case "ENQ":
                    ParseENQ(parts, result);
                    break;

                case "IND":
                    ParseIND(parts, result);
                    break;

                case "ACK":
                    ParseACK(parts, result);
                    break;

                case "prn":
                    ParsePRN(parts, result);
                    break;
                case "LAB":
                    ParseLAB(parts, result);
                    break;
                case "END":
                    ParseEND(parts, result);
                    break;
                case "REM":
                    ParseREM(parts, result);
                    break;
            }

            return result;
        }

        private void ParseENQ(string[] parts, MessageProcessingResult result)
        {
            if (parts.Length >= 3)
                result.Counter = parts[2].Trim();

            if (parts.Length >= 4)
                result.Barcode = parts[3].Trim();

            if (parts.Length >= 5)
                result.Source = parts[4].Trim();
        }
        private void ParseIND(string[] parts, MessageProcessingResult result)
        {
            // MachineId|IND|Counter|Reference|
            if (parts.Length >= 3) result.Counter = parts[2].Trim();
            if (parts.Length >= 4) result.Reference = parts[3].Trim();
        }

        private void ParseACK(string[] parts, MessageProcessingResult result)
        {
            // MachineId|ACK|Counter|Reference|Result|
            if (parts.Length >= 3) result.Counter = parts[2].Trim();
            if (parts.Length >= 4) result.Reference = parts[3].Trim();
            if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out int ack))
                result.AckResult = ack;
        }
        private void ParsePRN(string[] parts, MessageProcessingResult result)
        {
            // Expected format:
            // MachineId|prn|Counter|Reference|Result|

            if (parts.Length >= 3)
                result.Counter = parts[2].Trim();

            if (parts.Length >= 4)
                result.Reference = parts[3].Trim();

            if (parts.Length >= 5 && int.TryParse(parts[4], out int prnResult))
                result.PrnResult = prnResult;
        }
        private void ParseLAB(string[] parts, MessageProcessingResult result)
        {
            // Expected:
            // MachineId|LAB|Counter|Reference|Status|Event|SizeH|SizeL|SizeW|Area|...optional ** fields...|

            if (parts.Length >= 3) result.Counter = parts[2].Trim();
            if (parts.Length >= 4) result.Reference = parts[3].Trim();

            if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out int status))
                result.LabStatus = status;

            if (parts.Length >= 6) result.LabEvent = parts[5].Trim();

            if (parts.Length >= 7 && int.TryParse(parts[6].Trim(), out int h))
                result.LabSizeH = h;

            if (parts.Length >= 8 && int.TryParse(parts[7].Trim(), out int l))
                result.LabSizeL = l;

            if (parts.Length >= 9 && int.TryParse(parts[8].Trim(), out int w))
                result.LabSizeW = w;

            if (parts.Length >= 10 && int.TryParse(parts[9].Trim(), out int area))
                result.LabArea = area;

            if (parts.Length >= 12 && int.TryParse(parts[11].Trim(), out int carton))
                result.LabWeightCarton = carton;

            if (parts.Length >= 13 && int.TryParse(parts[12].Trim(), out int scale))
                result.LabWeightScale = scale;


        }
        private void ParseEND(string[] parts, MessageProcessingResult result)
        {
            // MachineId|END|Counter|Reference|Status|Event|SizeH|SizeL|SizeW|Area|...optional ** fields...|

            if (parts.Length >= 3) result.Counter = parts[2].Trim();
            if (parts.Length >= 4) result.Reference = parts[3].Trim();

            if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out int status))
                result.EndStatus = status;

            if (parts.Length >= 6) result.EndEvent = parts[5].Trim();

            if (parts.Length >= 7 && int.TryParse(parts[6].Trim(), out int h))
                result.EndSizeH = h;

            if (parts.Length >= 8 && int.TryParse(parts[7].Trim(), out int l))
                result.EndSizeL = l;

            if (parts.Length >= 9 && int.TryParse(parts[8].Trim(), out int w))
                result.EndSizeW = w;

            if (parts.Length >= 10 && int.TryParse(parts[9].Trim(), out int area))
                result.EndArea = area;

            if (parts.Length >= 12 && int.TryParse(parts[11].Trim(), out int carton))
                result.EndWeightCarton = carton;

            if (parts.Length >= 13 && int.TryParse(parts[12].Trim(), out int scale))
                result.EndWeightScale = scale;


        }
        private void ParseREM(string[] parts, MessageProcessingResult result)
        {
            // MachineId|REM|Counter|Reference|
            if (parts.Length >= 3) result.Counter = parts[2].Trim();
            if (parts.Length >= 4) result.Reference = parts[3].Trim();
        }
        public string FormatHeartbeatRequest(string machineId)
        {
            return $"{machineId}|HBT";
        }
        public string FormatHeartbeatResponse(string machineId)
        {
            return $"{machineId}|hbt";
        }

        public string FormatEnqResponse(
            string machineId,
            string counter,
            string barcode,
            string jobNumber,
            int result01,
            string matchLab,
            string packJobId,
            string description = null)
        {
            string reference = packJobId ?? "";
            description = description ?? jobNumber ?? "";
            string matchLabSafe = matchLab ?? "";

            return string.Join("|", new[]
            {
                machineId ?? "",
                "enq",
                counter ?? "",
                reference,
                result01.ToString(),
                "",
                "",
                "",
                barcode ?? "",
                "",
                matchLabSafe,
                description,
                "",
                "",
                "",
                "",
                "",
                ""
            });
        }

        public string FormatIndResponse(string machineId, string counter, string reference)
        {
           
            return string.Join("|", new[]
            {
                machineId ?? "",
                "ind",
                counter ?? "",
                reference ?? "",
                "" 
            });
        }
        public string FormatAckResponse(string machineId, string counter, string reference)
        {
            
            return string.Join("|", new[] { machineId ?? "", "ack", counter ?? "", reference ?? "", "" });
        }
        public string FormatPrnRequest(string machineId, string cisCounter, string reference, string labelData)
        {
          
            int lenBytes = Encoding.ASCII.GetByteCount(labelData ?? "");

            
            return string.Join("|", new[]
            {
                machineId ?? "",
                "PRN",
                cisCounter ?? "",
                reference ?? "",
                lenBytes.ToString(),
                labelData ?? "",
                "" 
            });
        }
        public string FormatLabResponse(
            string machineId,
            string counter,
            string reference,
            int result,           // 0/1
            string matchLab,      // blank if null
            //int flagToReject,     // 0/1
            string flagToReject,     // 0/1
            string sorter         // blank
        )
        {
           
            return string.Join("|", new[]
            {
                machineId ?? "",
                "lab",
                counter ?? "",
                reference ?? "",
                result.ToString(),
                matchLab ?? "",
                //flagToReject.ToString(),
                flagToReject,
                sorter ?? "",
                ""
            });
        }
        public string FormatEndResponse(string machineId, string counter, string reference)
        {
            // MachineId|end|Counter|Reference|
            return string.Join("|", new[]
            {
                machineId ?? "",
                "end",
                counter ?? "",
                reference ?? "",
                ""
            });
        }
        public string FormatRemResponse(string machineId, string counter, string reference)
        {
            // MachineId|rem|Counter|Reference|
            return string.Join("|", new[]
            {
                machineId ?? "",
                "rem",
                counter ?? "",
                reference ?? "",
                ""
            });
        }
    }
}
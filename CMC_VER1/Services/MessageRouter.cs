//using CMC_VER1.Models;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using CMC_VER1.Handlers;

//namespace CMC_VER1.Services
//{
//    public static class MessageRouter
//    {
//        public static async Task<string> Handle(
//            MessageProcessingResult result,
//            NetworkManager networkManager,
//            FormatProcessMessages formatter)
//        {
//            switch (result.MessageType)
//            {
//                case "HBT":
//                    var handler = new HeartbeatHandler(networkManager, formatter);
//                    return await handler.Handle(result);

//                default:
//                    return null;
//            }
//        }
//    }
//}
using CMC_VER1.Handlers;
using CMC_VER1.Models;
using CMC_VER1.Services;
using System;
using System.Threading.Tasks;

namespace CMC_VER1
{
    public static class MessageRouter
    {
        public static async Task Handle(
            MessageProcessingResult result,
            NetworkManager networkManager,
            FormatProcessMessages formatter,
            SQLManager sqlManager,
            Func<string> getJobNumber)
        {
            switch (result.MessageType)
            {
                case "HBT":
                    await networkManager.SendAsync(formatter.FormatHeartbeatResponse(result.MachineId));
                    break;
                case "hbt":
                   
                    break;
                case "ENQ":
                    var enq = new EnquiryHandler(networkManager, formatter, sqlManager, getJobNumber);
                    await enq.Handle(result);
                    break;

                case "IND":
                    var ind = new InductedHandler(networkManager, formatter);
                    await ind.Handle(result);
                    break;

                case "ACK":
                    var ack = new AckHandler(networkManager, formatter, sqlManager);
                    await ack.Handle(result);
                    break;

                case "prn":
                    await new PrnResultHandler(sqlManager).Handle(result);
                    break;
                case "LAB":
                    await new LabHandler(networkManager, formatter, sqlManager).Handle(result);
                    break;
                case "END":
                    await new EndHandler(networkManager, formatter, sqlManager).Handle(result);
                    break;
                case "REM":
                    await new RemHandler(networkManager, formatter, sqlManager).Handle(result);
                    break;
                default:
                    // ignore unknown messages for now
                    break;
            }
        }
    }
}
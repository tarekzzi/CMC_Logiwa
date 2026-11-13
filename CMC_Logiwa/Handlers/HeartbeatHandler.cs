using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    public class HeartbeatHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;

        public HeartbeatHandler(NetworkManager networkManager, FormatProcessMessages formatter)
        {
            _networkManager = networkManager;
            _formatter = formatter;
        }

        public async Task<string> Handle(MessageProcessingResult result)
        {
            // Only respond if it's uppercase HBT (request)
            if (!result.IsRequest)
                return null;

            string responsePayload = _formatter.FormatHeartbeatResponse(result.MachineId);

            await _networkManager.SendAsync(responsePayload);

            return null;
        }
    }
}

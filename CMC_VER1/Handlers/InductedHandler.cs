using CMC_VER1.Models;
using CMC_VER1.Services;
using System.Threading.Tasks;

namespace CMC_VER1.Handlers
{
    public class InductedHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;

        public InductedHandler(NetworkManager networkManager, FormatProcessMessages formatter)
        {
            _networkManager = networkManager;
            _formatter = formatter;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "IND")
                return;

         
            string payload = _formatter.FormatIndResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(payload);
        }
    }
}
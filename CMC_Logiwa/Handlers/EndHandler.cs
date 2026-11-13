using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// END (carton finished). The result, size and weight are stored locally by sp_Update_END.
    /// Nothing is sent to Logiwa from here: a successful END is queued in the local outbox and the
    /// background service pushes it to the web VM. The machine always gets its reply.
    /// </summary>
    public class EndHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public EndHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "END")
                return;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId },
                    { "@EndStatus", result.EndStatus },
                    { "@EndEvent", result.EndEvent ?? "" },
                    { "@EndSizeH", result.EndSizeH },
                    { "@EndSizeL", result.EndSizeL },
                    { "@EndSizeW", result.EndSizeW },
                    { "@EndArea", result.EndArea },
                    { "@EndWeightCarton", result.EndWeightCarton },
                    { "@EndWeightScale", result.EndWeightScale }
                };

                _sql.ExecuteStoredProcedure("dbo.sp_Update_END", parameters);
            }
            catch
            {
                // Swallowed on purpose: the machine must still get its reply.
            }

            string responsePayload = _formatter.FormatEndResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);
        }
    }
}

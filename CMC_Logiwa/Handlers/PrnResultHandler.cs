using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    public class PrnResultHandler
    {
        private readonly SQLManager _sql;

        public PrnResultHandler(SQLManager sql)
        {
            _sql = sql;
        }

        public Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "PRN")
                return Task.CompletedTask;

            if (!int.TryParse(result.Reference, out int packJobId))
                return Task.CompletedTask;

            var parameters = new Dictionary<string, object>
            {
                { "@PackJobID", packJobId },
                { "@PRNResult", result.PrnResult }
            };

            _sql.ExecuteStoredProcedure("dbo.sp_Update_PRN", parameters);

            return Task.CompletedTask;
        }
    }
}
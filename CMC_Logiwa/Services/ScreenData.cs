using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace CMC_Logiwa.Services
{
    /// <summary>The figures on the Overview tab.</summary>
    public sealed class DashboardCounts
    {
        public int ProcessedToday { get; set; }
        public int ProcessedWeek { get; set; }
        public int ProcessedMonth { get; set; }
        public int ProblemsToday { get; set; }
        public int ProblemsWeek { get; set; }
        public int ProblemsMonth { get; set; }
        public int Available { get; set; }
        public int NoLabel { get; set; }
        public int InProgress { get; set; }
    }

    /// <summary>One job that a scan would accept right now.</summary>
    public sealed class AvailableJob
    {
        public string TrackingNumber { get; set; }
        public string OrderCode { get; set; }
        public string ClientCode { get; set; }
        public DateTime ReceivedAt { get; set; }
        public string ReceivedText => ReceivedAt.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>One box that did not pack (Failed or Removed), with the reason the machine gave.</summary>
    public sealed class ErrorRow
    {
        public int PackJobId { get; set; }
        public string TrackingNumber { get; set; }
        public string OrderCode { get; set; }
        public string Status { get; set; }
        public DateTime EventAt { get; set; }
        public string EventCode { get; set; }
        public string Reason { get; set; }
        public string MachineId { get; set; }
        public string Operator { get; set; }
        public int? LengthMm { get; set; }
        public int? WidthMm { get; set; }
        public int? HeightMm { get; set; }
        public int? WeightG { get; set; }
        public bool IsCurrent { get; set; }
        public bool Archived { get; set; }

        public string EventAtText => EventAt.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>"300 x 200 x 100 mm, 3200 g", or empty when the machine measured nothing (a zero size is not a measurement).</summary>
        public string MeasuredText
        {
            get
            {
                bool hasSize = LengthMm > 0 && WidthMm > 0 && HeightMm > 0;
                bool hasWeight = WeightG > 0;
                if (!hasSize && !hasWeight) return "";
                string size = hasSize ? $"{LengthMm} x {WidthMm} x {HeightMm} mm" : "";
                string weight = hasWeight ? $"{WeightG} g" : "";
                return hasSize && hasWeight ? size + ", " + weight : size + weight;
            }
        }
    }

    /// <summary>One step in the history of a job.</summary>
    public sealed class JobEvent
    {
        public DateTime EventAt { get; set; }
        public string EventType { get; set; }
        public string Detail { get; set; }
        public string UserName { get; set; }
        public string MachineId { get; set; }
        public string EventAtText => EventAt.ToString("HH:mm:ss.fff");
        public string DateText => EventAt.ToString("yyyy-MM-dd");
    }

    /// <summary>Reads for the Overview and Errors tabs. Stored procedures only, all in db\004_procs_screens_dev.sql.</summary>
    public static class ScreenData
    {
        /// <summary>The first moment of today, of this week (Monday) and of this month, in local time.</summary>
        public static void PeriodStarts(DateTime now, out DateTime today, out DateTime week, out DateTime month)
        {
            today = now.Date;
            int sinceMonday = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0 ... Sunday = 6
            week = today.AddDays(-sinceMonday);
            month = new DateTime(today.Year, today.Month, 1);
        }

        public static DashboardCounts GetCounts(DateTime now)
        {
            PeriodStarts(now, out var today, out var week, out var month);
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Counts", new Dictionary<string, object>
            {
                { "@TodayStart", today }, { "@WeekStart", week }, { "@MonthStart", month }
            });

            var c = new DashboardCounts();
            if (dt.Rows.Count == 0) return c;
            DataRow r = dt.Rows[0];
            c.ProcessedToday = Int(r, "ProcessedToday");
            c.ProcessedWeek = Int(r, "ProcessedWeek");
            c.ProcessedMonth = Int(r, "ProcessedMonth");
            c.ProblemsToday = Int(r, "ProblemsToday");
            c.ProblemsWeek = Int(r, "ProblemsWeek");
            c.ProblemsMonth = Int(r, "ProblemsMonth");
            c.Available = Int(r, "Available");
            c.NoLabel = Int(r, "NoLabel");
            c.InProgress = Int(r, "InProgress");
            return c;
        }

        public static List<AvailableJob> GetAvailableJobs(int max)
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_List_AvailableJobs", new Dictionary<string, object> { { "@Max", max } });
            var list = new List<AvailableJob>();
            foreach (DataRow r in dt.Rows)
                list.Add(new AvailableJob
                {
                    TrackingNumber = Str(r, "TrackingNumber"),
                    OrderCode = Str(r, "OrderCode"),
                    ClientCode = Str(r, "ClientCode"),
                    ReceivedAt = Convert.ToDateTime(r["ReceivedAt"])
                });
            return list;
        }

        /// <param name="search">Part of a tracking number, job number or order code; empty = all.</param>
        /// <param name="fromDay">First day to include (local), or null.</param>
        /// <param name="toDay">Last day to include (local), or null.</param>
        public static List<ErrorRow> GetErrors(string search, DateTime? fromDay, DateTime? toDay, int max)
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_List_Errors", new Dictionary<string, object>
            {
                { "@Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() },
                { "@From", fromDay?.Date },
                { "@To", toDay?.Date.AddDays(1) },      // the procedure takes an exclusive end
                { "@Max", max }
            });

            var list = new List<ErrorRow>();
            foreach (DataRow r in dt.Rows)
                list.Add(new ErrorRow
                {
                    PackJobId = Convert.ToInt32(r["PackJobID"]),
                    TrackingNumber = Str(r, "TrackingNumber"),
                    OrderCode = Str(r, "OrderCode"),
                    Status = Str(r, "OrderStatus"),
                    EventAt = Convert.ToDateTime(r["EventAt"]),
                    EventCode = Str(r, "EventCode"),
                    Reason = Str(r, "Reason"),
                    MachineId = Str(r, "MachineId"),
                    Operator = Str(r, "Operator"),
                    LengthMm = NullableInt(r, "LengthMm"),
                    WidthMm = NullableInt(r, "WidthMm"),
                    HeightMm = NullableInt(r, "HeightMm"),
                    WeightG = NullableInt(r, "WeightG"),
                    IsCurrent = Convert.ToBoolean(r["IsCurrent"]),
                    Archived = Convert.ToBoolean(r["Archived"])
                });
            return list;
        }

        public static List<JobEvent> GetJobEvents(int packJobId)
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_List_PackJobEvents", new Dictionary<string, object> { { "@PackJobID", packJobId } });
            var list = new List<JobEvent>();
            foreach (DataRow r in dt.Rows)
                list.Add(new JobEvent
                {
                    EventAt = Convert.ToDateTime(r["EventAt"]),
                    EventType = Str(r, "EventType"),
                    Detail = Str(r, "Detail"),
                    UserName = Str(r, "UserName"),
                    MachineId = Str(r, "MachineId")
                });
            return list;
        }

        private static string Str(DataRow r, string col) => r[col] == DBNull.Value ? "" : r[col].ToString();
        private static int Int(DataRow r, string col) => r[col] == DBNull.Value ? 0 : Convert.ToInt32(r[col], CultureInfo.InvariantCulture);
        private static int? NullableInt(DataRow r, string col) => r[col] == DBNull.Value ? (int?)null : Convert.ToInt32(r[col], CultureInfo.InvariantCulture);
    }
}

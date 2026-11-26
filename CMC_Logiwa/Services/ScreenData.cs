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

    /// <summary>Totals of the dashboard for the chosen period and filters.</summary>
    public sealed class DashboardSummary
    {
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Removed { get; set; }
        public double? AvgSec { get; set; }      // average processing time (scan to END) of completed boxes
        public int? MinSec { get; set; }
        public int? MaxSec { get; set; }
        public DateTime? FirstAt { get; set; }
        public DateTime? LastAt { get; set; }
        public int ActiveHours { get; set; }     // clock hours in which at least one box was completed
        public DateTime? PeakHour { get; set; }
        public int PeakCount { get; set; }
        public int Problems => Failed + Removed;
    }

    /// <summary>One hour or one day of the dashboard charts.</summary>
    public sealed class SeriesPoint
    {
        public DateTime Start { get; set; }
        public int Completed { get; set; }
        public int Problems { get; set; }
        public double? AvgSec { get; set; }
    }

    /// <summary>One operator's share of the period.</summary>
    public sealed class OperatorRow
    {
        public string Operator { get; set; }
        public int Completed { get; set; }
        public int Problems { get; set; }
        public double? AvgSec { get; set; }
    }

    /// <summary>A quiet spell between two finished boxes.</summary>
    public sealed class StopRow
    {
        public DateTime StoppedAt { get; set; }
        public DateTime ResumedAt { get; set; }
        public int Seconds { get; set; }

        public string StoppedText => StoppedAt.ToString("yyyy-MM-dd HH:mm:ss");
        public string ResumedText => ResumedAt.ToString("yyyy-MM-dd HH:mm:ss");
        public string LengthText => Seconds >= 3600 ? $"{Seconds / 3600} h {Seconds % 3600 / 60} min" : $"{Seconds / 60} min {Seconds % 60} s";
    }

    /// <summary>One finished box in the dashboard list.</summary>
    public sealed class ProcessedRow
    {
        public int PackJobId { get; set; }
        public string TrackingNumber { get; set; }
        public string OrderCode { get; set; }
        public string ClientCode { get; set; }
        public string Status { get; set; }
        public DateTime FinishedAt { get; set; }
        public int? DurationSec { get; set; }
        public string MachineId { get; set; }
        public string Operator { get; set; }
        public int? LengthMm { get; set; }
        public int? WidthMm { get; set; }
        public int? HeightMm { get; set; }
        public int? WeightG { get; set; }

        public string FinishedText => FinishedAt.ToString("yyyy-MM-dd HH:mm:ss");
        public string DurationText => DurationSec.HasValue ? DurationSec.Value + " s" : "";
        public string SizeText => LengthMm > 0 && WidthMm > 0 && HeightMm > 0 ? $"{LengthMm} x {WidthMm} x {HeightMm}" : "";
        public string WeightText => WeightG > 0 ? WeightG + " g" : "";
    }

    /// <summary>The same moment is shown three ways: local time on this PC, UTC, and the time Logiwa recorded (UTC). Blank when unknown (boxes stored before the Logiwa time was kept).</summary>
    public static class TimeText
    {
        public static string Minutes(DateTime? t) => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm") : "";
    }

    /// <summary>One job that a scan would accept right now.</summary>
    public sealed class AvailableJob
    {
        public string TrackingNumber { get; set; }
        public string OrderCode { get; set; }
        public string ClientCode { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTime? ReceivedUtc { get; set; }
        public DateTime? LogiwaAt { get; set; }
        public string ReceivedText => ReceivedAt.ToString("yyyy-MM-dd HH:mm");
        public string ReceivedUtcText => TimeText.Minutes(ReceivedUtc);
        public string LogiwaText => TimeText.Minutes(LogiwaAt);
    }

    /// <summary>One box that did not pack (Failed or Removed), with the reason the machine gave.</summary>
    public sealed class ErrorRow
    {
        public int PackJobId { get; set; }
        public string TrackingNumber { get; set; }
        public string OrderCode { get; set; }
        public string Status { get; set; }
        public DateTime EventAt { get; set; }
        public DateTime? ReceivedAt { get; set; }
        public DateTime? ReceivedUtc { get; set; }
        public DateTime? LogiwaAt { get; set; }
        public string ReceivedText => TimeText.Minutes(ReceivedAt);
        public string ReceivedUtcText => TimeText.Minutes(ReceivedUtc);
        public string LogiwaText => TimeText.Minutes(LogiwaAt);
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
                    ReceivedAt = Convert.ToDateTime(r["ReceivedAt"]),
                    ReceivedUtc = NullableTime(r, "ReceivedUtc"),
                    LogiwaAt = NullableTime(r, "LogiwaAt")
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
                    ReceivedAt = NullableTime(r, "ReceivedAt"),
                    ReceivedUtc = NullableTime(r, "ReceivedUtc"),
                    LogiwaAt = NullableTime(r, "LogiwaAt"),
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

        // ===== Dashboard: speed and throughput (db\005_procs_dashboard_dev.sql) =====

        public static List<string> GetDashboardClients()
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Clients", null);
            var list = new List<string>();
            foreach (DataRow r in dt.Rows) list.Add(Str(r, "ClientCode"));
            return list;
        }

        public static DashboardSummary GetSummary(DateTime from, DateTime toExclusive, string search, string client)
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Summary", FilterArgs(from, toExclusive, search, client));
            var s = new DashboardSummary();
            if (dt.Rows.Count == 0) return s;
            DataRow r = dt.Rows[0];
            s.Completed = Int(r, "Completed");
            s.Failed = Int(r, "Failed");
            s.Removed = Int(r, "Removed");
            s.AvgSec = r["AvgSec"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["AvgSec"], CultureInfo.InvariantCulture);
            s.MinSec = NullableInt(r, "MinSec");
            s.MaxSec = NullableInt(r, "MaxSec");
            s.FirstAt = r["FirstAt"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["FirstAt"]);
            s.LastAt = r["LastAt"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["LastAt"]);
            s.ActiveHours = Int(r, "ActiveHours");
            s.PeakHour = r["PeakHour"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["PeakHour"]);
            s.PeakCount = Int(r, "PeakCount");
            return s;
        }

        /// <param name="hourly">true = one point per hour, false = one per day.</param>
        public static List<SeriesPoint> GetSeries(DateTime from, DateTime toExclusive, bool hourly, string search, string client)
        {
            var args = FilterArgs(from, toExclusive, search, client);
            args["@Bucket"] = hourly ? "H" : "D";
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Series", args);

            var found = new Dictionary<DateTime, SeriesPoint>();
            foreach (DataRow r in dt.Rows)
            {
                var p = new SeriesPoint
                {
                    Start = Convert.ToDateTime(r["BucketStart"]),
                    Completed = Int(r, "Completed"),
                    Problems = Int(r, "Problems"),
                    AvgSec = r["AvgSec"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["AvgSec"], CultureInfo.InvariantCulture)
                };
                found[p.Start] = p;
            }

            // fill the empty hours/days so the chart axis is continuous
            var list = new List<SeriesPoint>();
            DateTime t = hourly ? new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0) : from.Date;
            for (; t < toExclusive; t = hourly ? t.AddHours(1) : t.AddDays(1))
                list.Add(found.TryGetValue(t, out var hit) ? hit : new SeriesPoint { Start = t });
            return list;
        }

        public static List<ProcessedRow> GetProcessed(DateTime from, DateTime toExclusive, string search, string client, string status, int max)
        {
            var args = FilterArgs(from, toExclusive, search, client);
            args["@Status"] = string.IsNullOrWhiteSpace(status) ? null : status;
            args["@Max"] = max;
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_List_Processed", args);

            var list = new List<ProcessedRow>();
            foreach (DataRow r in dt.Rows)
                list.Add(new ProcessedRow
                {
                    PackJobId = Convert.ToInt32(r["PackJobID"]),
                    TrackingNumber = Str(r, "TrackingNumber"),
                    OrderCode = Str(r, "OrderCode"),
                    ClientCode = Str(r, "ClientCode"),
                    Status = Str(r, "OrderStatus"),
                    FinishedAt = Convert.ToDateTime(r["FinishedAt"]),
                    DurationSec = NullableInt(r, "DurationSec"),
                    MachineId = Str(r, "MachineId"),
                    Operator = Str(r, "Operator"),
                    LengthMm = NullableInt(r, "BoxSizeL"),
                    WidthMm = NullableInt(r, "BoxSizeW"),
                    HeightMm = NullableInt(r, "BoxSizeH"),
                    WeightG = NullableInt(r, "WeightScale")
                });
            return list;
        }

        public static List<OperatorRow> GetOperators(DateTime from, DateTime toExclusive, string search, string client)
        {
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Operators", FilterArgs(from, toExclusive, search, client));
            var list = new List<OperatorRow>();
            foreach (DataRow r in dt.Rows)
                list.Add(new OperatorRow
                {
                    Operator = Str(r, "Operator"),
                    Completed = Int(r, "Completed"),
                    Problems = Int(r, "Problems"),
                    AvgSec = r["AvgSec"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["AvgSec"], CultureInfo.InvariantCulture)
                });
            return list;
        }

        /// <param name="minMinutes">Shortest quiet spell that counts as a stop.</param>
        public static List<StopRow> GetStops(DateTime from, DateTime toExclusive, int minMinutes, string search, string client)
        {
            var args = FilterArgs(from, toExclusive, search, client);
            args["@MinMinutes"] = minMinutes;
            DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Dashboard_Stops", args);
            var list = new List<StopRow>();
            foreach (DataRow r in dt.Rows)
                list.Add(new StopRow
                {
                    StoppedAt = Convert.ToDateTime(r["StoppedAt"]),
                    ResumedAt = Convert.ToDateTime(r["ResumedAt"]),
                    Seconds = Convert.ToInt32(r["Seconds"], CultureInfo.InvariantCulture)
                });
            return list;
        }

        private static Dictionary<string, object> FilterArgs(DateTime from, DateTime toExclusive, string search, string client) =>
            new Dictionary<string, object>
            {
                { "@From", from },
                { "@To", toExclusive },
                { "@Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() },
                { "@Client", string.IsNullOrWhiteSpace(client) ? null : client }
            };

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

        private static DateTime? NullableTime(DataRow r, string col) => r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToDateTime(r[col]) : (DateTime?)null;
        private static string Str(DataRow r, string col) => r[col] == DBNull.Value ? "" : r[col].ToString();
        private static int Int(DataRow r, string col) => r[col] == DBNull.Value ? 0 : Convert.ToInt32(r[col], CultureInfo.InvariantCulture);
        private static int? NullableInt(DataRow r, string col) => r[col] == DBNull.Value ? (int?)null : Convert.ToInt32(r[col], CultureInfo.InvariantCulture);
    }
}

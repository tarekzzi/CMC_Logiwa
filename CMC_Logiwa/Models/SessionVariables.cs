namespace CMC_Logiwa.Models
{
    /// <summary>Who is signed in on this PC (set by the login window, read by the handlers to stamp records).</summary>
    public static class SessionVariables
    {
        public static string UserName { get; set; }
    }
}

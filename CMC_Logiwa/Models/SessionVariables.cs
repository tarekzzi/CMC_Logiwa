namespace CMC_Logiwa.Models
{
    /// <summary>Who is signed in on this PC (set by the login window, read by the handlers to stamp records).</summary>
    public static class SessionVariables
    {
        public static string UserName { get; set; }

        /// <summary>Administrators manage users and every setting; operators run the machine and change their own password.</summary>
        public static bool IsAdmin { get; set; }
    }
}

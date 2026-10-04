namespace TalkWatch.Web.Services;

public static class DatabaseConnection
{
    /// <summary>
    /// The PostgreSQL connection string TalkWatch uses. <paramref name="password"/>, when set, replaces any password in
    /// the string: it usually comes from a secret file (Database__Password), so the connection string itself can stay
    /// in plain configuration.
    /// </summary>
    public static string Build(string connectionString, string? password)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        // Npgsql tries Kerberos (GSS) encryption first by default, and the image has no Kerberos libraries, so every
        // connection logged an error. Off unless the connection string asks for it.
        if (!connectionString.Contains("gss", StringComparison.OrdinalIgnoreCase))
        {
            builder.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }
}

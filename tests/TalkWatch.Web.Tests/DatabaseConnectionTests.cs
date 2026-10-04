using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public class DatabaseConnectionTests
{
    [Fact]
    public void A_password_from_a_secret_file_is_merged_in()
    {
        var built = new Npgsql.NpgsqlConnectionStringBuilder(DatabaseConnection.Build("Host=postgres;Database=talkwatch;Username=talkwatch_app", "s3cret;with=odd chars"));

        Assert.Equal("s3cret;with=odd chars", built.Password);
        Assert.Equal("talkwatch_app", built.Username);
    }

    [Fact]
    public void A_password_in_the_string_stays_when_no_secret_is_given() =>
        Assert.Equal("inline", new Npgsql.NpgsqlConnectionStringBuilder(DatabaseConnection.Build("Host=db;Password=inline", null)).Password);

    [Fact]
    public void Kerberos_is_off_unless_asked_for()
    {
        Assert.Equal(Npgsql.GssEncryptionMode.Disable, new Npgsql.NpgsqlConnectionStringBuilder(DatabaseConnection.Build("Host=db", null)).GssEncryptionMode);
        Assert.Equal(Npgsql.GssEncryptionMode.Require, new Npgsql.NpgsqlConnectionStringBuilder(DatabaseConnection.Build("Host=db;Gss Encryption Mode=Require", null)).GssEncryptionMode);
    }
}

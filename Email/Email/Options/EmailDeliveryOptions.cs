namespace Email.Options;

public sealed class EmailDeliveryOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool EnableSsl { get; set; } = false;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DefaultFromAddress { get; set; } = "no-reply@free-ebay.com";
    public int SmtpTimeoutSeconds { get; set; } = 30;
}
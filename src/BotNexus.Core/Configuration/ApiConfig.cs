namespace BotNexus.Core.Configuration;

/// <summary>OpenAI-compatible REST API configuration.</summary>
public class ApiConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8900;
    public double Timeout { get; set; } = 120.0;
    public bool Enabled { get; set; } = false;
}

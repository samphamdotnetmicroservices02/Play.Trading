namespace Play.Trading.Service.Settings;

public class SeqSettings
{
    public string Host { get; init; }
    public string Port { get; init; }
    public string ServerUrl { get { return $"http://{Host}:{Port}"; } }
}
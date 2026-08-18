namespace WebDataStudio.Server.Drivers.Abstractions;

/// Static description of an engine, used by the connection form and the UI icons.
public sealed record DriverInfo(
    string Id,
    string Label,
    int DefaultPort,
    string ConnectionStringTemplate);

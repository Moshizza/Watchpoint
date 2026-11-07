using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Internal;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Storage.Tests;

internal static class StorageTestHost
{
    public static StorageSettings Settings(params (string Key, string? Value)[] values) =>
        StorageSettings.Bind(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    public static StoreMapHolder Holder(StorageSettings settings, bool isDevelopment = true, params IStorageProvider[] providers) =>
        new(settings, providers.Length > 0 ? providers : [new FakeStorageProvider()], new StorageEnvironment(isDevelopment));

    public static ILogger<T> Logger<T>(LogCollector logs)
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        return factory.CreateLogger<T>();
    }
}

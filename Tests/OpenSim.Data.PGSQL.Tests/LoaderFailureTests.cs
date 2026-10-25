using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Server.Base;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public interface ILoaderProbe { }
public abstract class AbstractProbe : ILoaderProbe { }
public class GenericProbe<T> : ILoaderProbe { }
public class ParameterlessProbe : ILoaderProbe { }
public class WorkingProbe : ILoaderProbe { public WorkingProbe(string argument) { } }
public class BrokenProbe : ILoaderProbe
{
    public BrokenProbe(string argument) => throw new ReflectionTypeLoadException(
        new Type[] { null }, new Exception[] { new FileNotFoundException("missing-provider-dependency.dll") });
}

public class LoaderFailureTests
{
    [Fact]
    public void NullArgumentsMeanParameterlessConstructor()
    {
        var result = StoragePluginLoader.Load<ILoaderProbe>(GetType().Assembly.Location,
            nameof(ParameterlessProbe), null, new CaptureLogger());
        Assert.IsType<ParameterlessProbe>(result);
    }

    [Fact]
    public void ConstructorLoaderExceptionsAreLoggedWithoutArgumentValues()
    {
        var logger = new CaptureLogger();
        var result = StoragePluginLoader.Load<ILoaderProbe>(GetType().Assembly.Location,
            nameof(BrokenProbe), new object[] { "password-do-not-log" }, logger);
        Assert.Null(result);
        Assert.Contains(logger.Messages, message => message.Contains("missing-provider-dependency.dll"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("password-do-not-log"));
    }

    [Fact]
    public void AbstractAndOpenGenericTypesCannotShadowConcreteProvider()
    {
        var result = StoragePluginLoader.Load<ILoaderProbe>(GetType().Assembly.Location,
            nameof(WorkingProbe), new object[] { "test" }, new CaptureLogger());
        Assert.IsType<WorkingProbe>(result);
    }

    [Fact]
    public void RequiredServiceLoadFailureThrowsInsteadOfRegisteringNullHandler()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServerUtils.LoadRequiredPlugin<ILoaderProbe>("missing-provider.dll", Array.Empty<object>()));
    }

    [Fact]
    public void SameNamedContractFromAnotherLoadContextIsRejectedAndDiagnosed()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("foreign-contract", isCollectible: true);
        try
        {
            Type foreignContract = context.LoadFromAssemblyPath(GetType().Assembly.Location)
                .GetType(typeof(ILoaderProbe).FullName, throwOnError: true);
            var logger = new CaptureLogger();
            object result = typeof(StoragePluginLoader).GetMethod(nameof(StoragePluginLoader.Load))
                .MakeGenericMethod(foreignContract).Invoke(null, new object[] {
                    GetType().Assembly.Location, nameof(WorkingProbe), new object[] { "test" }, logger });
            Assert.Null(result);
            Assert.Contains(logger.Messages, message => message.Contains("different contract identity"));
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }
}

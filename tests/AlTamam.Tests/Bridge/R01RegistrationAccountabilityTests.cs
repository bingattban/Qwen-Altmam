using System.Reflection;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

// Exercise the registration factory directly without mutating the global
// canonical manifest or interfering with concurrently running dispatcher tests.
public sealed class R01RegistrationAccountabilityTests
{
    [Fact]
    public async Task HandlerFactory_RejectsUnimplementedActionInsteadOfRegisteringGenericFallback()
    {
        string path = SyntheticFixtures.CreateTempDatabasePath();
        try
        {
            using var db = new AlTamamDbContext(path, SyntheticFixtures.TestKeyHex,
                useConnectionPooling: false);
            await db.InitializeAsync();
            var factory = typeof(BridgeHandlerCatalog).GetMethod("BuildHandler",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(factory);
            var error = Assert.Throws<TargetInvocationException>(() =>
                factory!.Invoke(null, new object[] {
                    "R01RequiredActionWithoutImplementation", new BridgeSharedServices(db)
                }));
            Assert.IsType<InvalidOperationException>(error.InnerException);
        }
        finally
        {
            SyntheticFixtures.DeleteTempDatabase(path);
        }
    }
}

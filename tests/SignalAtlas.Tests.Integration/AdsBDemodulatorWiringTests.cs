using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Integration;

/// <summary>
/// The live decode stage only determines devices if the demodulator + decode collaborators are
/// registered. Asserts DI provides them so BuildPipeline / PipelineHostedService can inject them.
/// </summary>
public class AdsBDemodulatorWiringTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void AdsBDemodulator_IsRegisteredAsIDemodulator()
    {
        using var scope = factory.Services.CreateScope();
        var demods = scope.ServiceProvider.GetServices<IDemodulator>();
        Assert.Contains(demods, d => d is AdsBDemodulator);
    }

    [Fact]
    public void DecodeStageCollaborators_AreResolvable()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetService<IDecoderRegistry>());
        Assert.NotNull(sp.GetService<IDeviceResolver>());
        Assert.NotNull(sp.GetService<IDeviceRepository>());
    }
}

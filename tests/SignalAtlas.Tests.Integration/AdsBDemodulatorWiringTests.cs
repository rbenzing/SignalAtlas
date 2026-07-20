using System.Linq;
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
    public void AdsBDemodulator_IsTransient_ResolvingTwiceYieldsDifferentInstances()
    {
        // AdsBDemodulator is now STATEFUL (retains an inter-block sample carry per stream), so it
        // must be registered Transient, not Singleton — a shared singleton across concurrent
        // /ingest/iq streams would interleave carry-over state and reintroduce the cross-stream race
        // this lifetime change fixes. Lock the lifetime so a future refactor can't silently revert it.
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var first = sp.GetServices<IDemodulator>().OfType<AdsBDemodulator>().Single();
        var second = sp.GetServices<IDemodulator>().OfType<AdsBDemodulator>().Single();
        Assert.NotSame(first, second);
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

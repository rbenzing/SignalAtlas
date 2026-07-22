using SignalAtlas.Domain;
using SignalAtlas.Persistence;
using Xunit;

public class AptImageStoreTests
{
    [Fact]
    public void PutThenGet_ReturnsBytes()
    {
        IAptImageStore s = new AptImageStore();
        s.Put("a", [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Get("a"));
        Assert.Null(s.Get("missing"));
    }

    [Fact]
    public void Put_RefreshesBytesForSameKey()
    {
        IAptImageStore s = new AptImageStore();
        s.Put("a", [1]);
        s.Put("a", [9, 9]);
        Assert.Equal(new byte[] { 9, 9 }, s.Get("a"));
    }

    [Fact]
    public void Put_EvictsLeastRecentlyPutBeyondCapacity()
    {
        IAptImageStore s = new AptImageStore(capacity: 2);
        s.Put("a", [1]);
        s.Put("b", [2]);
        s.Put("c", [3]);      // evicts "a"
        Assert.Null(s.Get("a"));
        Assert.NotNull(s.Get("b"));
        Assert.NotNull(s.Get("c"));
    }
}

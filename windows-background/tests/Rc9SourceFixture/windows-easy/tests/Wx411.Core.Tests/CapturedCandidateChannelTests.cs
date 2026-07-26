namespace Wx411.Core.Tests;

public sealed class CapturedCandidateChannelTests
{
    [Fact]
    public void ConstantsMatchTheRc9SensitiveQueueBoundary()
    {
        Assert.Equal(64, CapturedCandidateChannel.Capacity);
        Assert.Equal(4096, CapturedCandidateChannel.MaxPayloadBytes);
    }

    [Fact]
    public async Task OverflowIsStructuredAndUnreadCandidatesAreCleared()
    {
        await using var channel = new CapturedCandidateChannel();
        var buffers = new List<byte[]>();
        for (var index = 0; index < CapturedCandidateChannel.Capacity; index++)
        {
            var candidate = Candidate((byte)(index + 1));
            buffers.Add(candidate.KeyData!);
            Assert.True(channel.TryWrite(candidate));
        }

        using var overflow = Candidate(0xEE);
        var overflowBuffer = overflow.KeyData!;
        Assert.False(channel.TryWrite(overflow));
        Assert.Equal(CaptureSessionErrorKind.CandidateQueueOverflow, channel.Error?.Kind);
        Assert.All(overflowBuffer, value => Assert.Equal(0, value));

        await channel.DisposeAsync();
        foreach (var buffer in buffers)
            Assert.All(buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReadTransfersOwnershipInFifoOrder()
    {
        await using var channel = new CapturedCandidateChannel();
        Assert.True(channel.TryWrite(Candidate(1)));
        Assert.True(channel.TryWrite(Candidate(2)));
        channel.Complete();

        var values = new List<byte>();
        await foreach (var candidate in channel.ReadAllAsync())
        {
            using (candidate)
                values.Add(candidate.KeyData![0]);
        }

        Assert.Equal([1, 2], values);
    }

    [Fact]
    public void OversizedPayloadIsClearedAndRejected()
    {
        using var candidate = Candidate(7, CapturedCandidateChannel.MaxPayloadBytes + 1);
        var buffer = candidate.KeyData!;
        using var channel = new CapturedCandidateChannel();

        Assert.False(channel.TryWrite(candidate));
        Assert.All(buffer, value => Assert.Equal(0, value));
    }

    private static CapturedKeyMaterial Candidate(byte value, int length = 32)
    {
        var data = Enumerable.Repeat(value, length).ToArray();
        return new CapturedKeyMaterial("test", 1, string.Empty, 1, DateTime.UtcNow)
        {
            KeyData = data,
            KeyLength = data.Length,
        };
    }
}

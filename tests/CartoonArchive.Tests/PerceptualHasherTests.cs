using CartoonArchive.Storage;

namespace CartoonArchive.Tests;

/// <summary>
/// Hamming distance drives the near-duplicate threshold in HandleUpload, so an off-by-one
/// here shows up as either missed duplicates or false warnings on unrelated images.
/// </summary>
public class PerceptualHasherTests
{
    [Fact]
    public void Identical_hashes_are_zero_apart()
    {
        Assert.Equal(0, PerceptualHasher.HammingDistance(0b1011, 0b1011));
        Assert.Equal(0, PerceptualHasher.HammingDistance(0, 0));
        Assert.Equal(0, PerceptualHasher.HammingDistance(-1, -1));
    }

    [Theory]
    [InlineData(0b0000, 0b0001, 1)]
    [InlineData(0b0000, 0b0011, 2)]
    [InlineData(0b1010, 0b0101, 4)]
    [InlineData(0b1111, 0b0000, 4)]
    public void Counts_differing_bits(long a, long b, int expected)
    {
        Assert.Equal(expected, PerceptualHasher.HammingDistance(a, b));
    }

    [Fact]
    public void Counts_all_64_bits_when_fully_inverted()
    {
        // -1 is all ones. The cast to ulong inside the method is what keeps the sign bit
        // from being lost; without it this would come back as 63 or throw.
        Assert.Equal(64, PerceptualHasher.HammingDistance(0, -1));
    }

    [Fact]
    public void Counts_the_sign_bit()
    {
        Assert.Equal(1, PerceptualHasher.HammingDistance(0, long.MinValue));
    }

    [Fact]
    public void Is_symmetric()
    {
        const long a = 0x0F1E2D3C4B5A6978;
        const long b = unchecked((long)0xF0E1D2C3B4A59687);
        Assert.Equal(
            PerceptualHasher.HammingDistance(a, b),
            PerceptualHasher.HammingDistance(b, a)
        );
    }
}

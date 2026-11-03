using ScanApp.Core.Model;

namespace ScanApp.Core.Tests.Model;

public class ProfileTests
{
    [Fact]
    public void Profile_ConstructorRejectsMismatchedArrayLengths()
    {
        Assert.Throws<ArgumentException>(() => new Profile(0, 0, new float[3], new float[2], null));
        Assert.Throws<ArgumentException>(() => new Profile(0, 0, new float[3], new float[3], new ushort[2]));
    }
}

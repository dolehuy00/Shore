using Microsoft.Extensions.Logging.Abstractions;

namespace Shorekeeper.Platform.Windows.Tests;

public class ActiveDirectoryInfoTests
{
    [Fact]
    public void Answers_without_throwing_inside_or_outside_a_domain()
    {
        var directory = new ActiveDirectoryInfo(NullLogger<ActiveDirectoryInfo>.Instance);

        // Outside a domain: null at once. Inside: the user's entry, or null when no domain controller answers.
        if (directory.GetCurrentUser() is { } profile)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
        }
    }
}

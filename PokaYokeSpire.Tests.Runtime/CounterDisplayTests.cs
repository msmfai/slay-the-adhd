using PokaYokeSpire.Relics;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

public class CounterDisplayTests
{
    [Fact] public void CurrentOnly_ShowsNumber() => Assert.Equal("2", new CounterDisplay(2).Text);
    [Fact] public void WithMax_ShowsSlash() => Assert.Equal("2/3", new CounterDisplay(2, 3).Text);
    [Fact] public void ZeroMax_StillShowsSlash() => Assert.Equal("0/0", new CounterDisplay(0, 0).Text);
    [Fact] public void NegativeCurrent_Renders() => Assert.Equal("-1", new CounterDisplay(-1).Text);
}

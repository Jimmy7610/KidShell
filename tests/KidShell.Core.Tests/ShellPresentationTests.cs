using KidShell.Core.Runtime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// How the window is presented, and why that is not a security control.
///
/// These tests describe a presentation rule. They are here rather than buried
/// in the app because two of them are really statements about the product's
/// honesty: a developer must never be trapped, and full screen must never be
/// mistaken for containment.
/// </summary>
public class ShellPresentationTests
{
    [Fact]
    public void A_shipped_build_shows_the_child_a_full_screen()
    {
        Assert.Equal(WindowPresentation.FullScreen,
            ShellPresentation.Decide(RuntimeEnvironment.Production, isChildMode: true));
    }

    [Fact]
    public void A_developer_build_is_never_full_screen()
    {
        // A borderless full-screen window with no title bar, on the machine
        // somebody is writing the code on, is a bad afternoon.
        Assert.Equal(WindowPresentation.Windowed,
            ShellPresentation.Decide(RuntimeEnvironment.Development, isChildMode: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_developer_build_is_windowed_in_every_mode(bool isChildMode)
    {
        Assert.Equal(WindowPresentation.Windowed,
            ShellPresentation.Decide(RuntimeEnvironment.Development, isChildMode));
    }

    [Fact]
    public void Parent_mode_stays_windowed_even_in_a_shipped_build()
    {
        // An adult needs the ordinary window controls while they work, and
        // first-run setup is something an adult does.
        Assert.Equal(WindowPresentation.Windowed,
            ShellPresentation.Decide(RuntimeEnvironment.Production, isChildMode: false));
    }

    [Fact]
    public void The_decision_comes_only_from_the_build_and_the_mode()
    {
        // Two inputs, no configuration. There is deliberately no setting a
        // child could find that turns the window back into a resizable one,
        // and no setting a parent could set that traps a developer.
        var method = typeof(ShellPresentation).GetMethod(nameof(ShellPresentation.Decide));

        Assert.NotNull(method);
        Assert.Equal(2, method!.GetParameters().Length);
    }
}

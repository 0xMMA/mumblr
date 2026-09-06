using Mumblr.App.Attention;

namespace Mumblr.App.Tests;

/// <summary>
/// The focus logic behind the taskbar flash, without a window: attention is wanted between
/// Begin and End, and the taskbar flashes exactly while it is wanted and the window is not in
/// front.
/// </summary>
public class WindowAttentionTests
{
    private bool active = true;
    private readonly List<bool> flashes = [];

    private WindowAttention Create() => new(() => active, flashing => flashes.Add(flashing));

    [Fact]
    public void Begin_flashes_only_when_the_window_is_not_in_front()
    {
        var attention = Create();

        attention.Begin();
        flashes.ShouldBeEmpty();

        active = false;
        Create().Begin();
        flashes.ShouldBe([true]);
    }

    [Fact]
    public void Losing_focus_while_wanted_flashes_and_coming_back_stops_it()
    {
        var attention = Create();
        attention.Begin();

        active = false;
        attention.WindowDeactivated();
        active = true;
        attention.WindowActivated();

        flashes.ShouldBe([true, false]);
    }

    [Fact]
    public void End_stops_the_flashing_whatever_the_focus()
    {
        active = false;
        var attention = Create();
        attention.Begin();

        attention.End();

        flashes.ShouldBe([true, false]);
    }

    [Fact]
    public void Focus_changes_while_nothing_is_wanted_do_nothing()
    {
        var attention = Create();

        active = false;
        attention.WindowDeactivated();
        active = true;
        attention.WindowActivated();

        flashes.ShouldBeEmpty();
    }
}

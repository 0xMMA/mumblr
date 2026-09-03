using Mumblr.Core.State;

namespace Mumblr.Core.Tests;

public class SessionStateMachineTests
{
    [Fact]
    public void Starts_idle_with_editor_unlocked()
    {
        var machine = new SessionStateMachine();

        machine.State.ShouldBe(SessionState.Idle);
        machine.IsEditorLocked.ShouldBeFalse();
    }

    [Fact]
    public void Recording_locks_the_editor()
    {
        var machine = new SessionStateMachine();

        machine.TryStartRecording().ShouldBeTrue();

        machine.State.ShouldBe(SessionState.Recording);
        machine.IsEditorLocked.ShouldBeTrue();
    }

    [Fact]
    public void Recording_cannot_start_twice()
    {
        var machine = new SessionStateMachine();
        machine.TryStartRecording();

        machine.TryStartRecording().ShouldBeFalse();
    }

    [Fact]
    public void Command_from_idle_returns_to_idle()
    {
        var machine = new SessionStateMachine();

        machine.TryStartCommand().ShouldBeTrue();
        machine.State.ShouldBe(SessionState.Commanding);
        machine.RecordingPausedForCommand.ShouldBeFalse();

        machine.TryFinishCommand().ShouldBeTrue();
        machine.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Command_from_recording_resumes_recording()
    {
        var machine = new SessionStateMachine();
        machine.TryStartRecording();

        machine.TryStartCommand().ShouldBeTrue();
        machine.RecordingPausedForCommand.ShouldBeTrue();

        machine.TryFinishCommand().ShouldBeTrue();
        machine.State.ShouldBe(SessionState.Recording);
    }

    [Fact]
    public void Commanding_blocks_a_second_command()
    {
        var machine = new SessionStateMachine();
        machine.TryStartCommand();

        machine.TryStartCommand().ShouldBeFalse();
    }

    [Fact]
    public void Commanding_blocks_recording_transitions()
    {
        var machine = new SessionStateMachine();
        machine.TryStartCommand();

        machine.TryStartRecording().ShouldBeFalse();
        machine.TryStopRecording().ShouldBeFalse();
        machine.State.ShouldBe(SessionState.Commanding);
    }

    [Fact]
    public void Finishing_a_command_that_never_started_is_rejected()
    {
        var machine = new SessionStateMachine();

        machine.TryFinishCommand().ShouldBeFalse();
    }

    [Fact]
    public void Raises_state_changed_with_previous_and_next()
    {
        var machine = new SessionStateMachine();
        var transitions = new List<(SessionState From, SessionState To)>();
        machine.StateChanged += (from, to) => transitions.Add((from, to));

        machine.TryStartRecording();
        machine.TryStopRecording();

        transitions.ShouldBe([(SessionState.Idle, SessionState.Recording), (SessionState.Recording, SessionState.Idle)]);
    }
}

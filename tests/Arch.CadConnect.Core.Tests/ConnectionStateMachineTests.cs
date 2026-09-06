using Arch.CadConnect.Core.Connection;

namespace Arch.CadConnect.Core.Tests;

public class ConnectionStateMachineTests
{
    [Fact]
    public void Happy_path_signed_out_to_connected_to_signed_out()
    {
        var m = new ConnectionStateMachine();
        var changes = new List<ConnectionStateChange>();
        m.Changed += changes.Add;

        Assert.Equal(ConnectionState.SignedOut, m.State);
        m.BeginSignIn();
        Assert.Equal(ConnectionState.Connecting, m.State);
        m.SignInSucceeded();
        Assert.Equal(ConnectionState.Connected, m.State);
        m.SignOut();
        Assert.Equal(ConnectionState.SignedOut, m.State);

        Assert.Equal(
            new[] { "BeginSignIn", "SignInSucceeded", "SignOut" },
            changes.Select(c => c.Trigger));
    }

    [Fact]
    public void Rejected_from_connecting_goes_unauthorized_and_can_retry()
    {
        var m = new ConnectionStateMachine();
        m.BeginSignIn();
        m.Rejected();
        Assert.Equal(ConnectionState.Unauthorized, m.State);

        m.BeginSignIn(); // retry allowed from Unauthorized
        Assert.Equal(ConnectionState.Connecting, m.State);
    }

    [Fact]
    public void Server_unreachable_keeps_a_retry_path_and_can_recover()
    {
        var m = new ConnectionStateMachine();
        m.BeginSignIn();
        m.SignInSucceeded();
        m.ServerUnreachable();
        Assert.Equal(ConnectionState.ServerUnavailable, m.State);

        m.SessionConfirmed(); // server came back
        Assert.Equal(ConnectionState.Connected, m.State);
    }

    [Fact]
    public void Illegal_transitions_throw_rather_than_silently_no_op()
    {
        var m = new ConnectionStateMachine();
        Assert.Throws<InvalidOperationException>(() => m.SignInSucceeded()); // not Connecting
        Assert.Throws<InvalidOperationException>(() => m.Rejected());        // SignedOut
    }

    [Fact]
    public void SignOut_is_always_legal()
    {
        foreach (var initial in Enum.GetValues<ConnectionState>())
        {
            var m = new ConnectionStateMachine(initial);
            m.SignOut();
            Assert.Equal(ConnectionState.SignedOut, m.State);
        }
    }

    [Fact]
    public void No_event_for_a_no_op_transition()
    {
        var m = new ConnectionStateMachine(ConnectionState.Connected);
        var fired = 0;
        m.Changed += _ => fired++;
        m.SessionConfirmed(); // already Connected
        Assert.Equal(0, fired);
    }

    [Theory]
    [InlineData(ConnectionState.SignedOut, false, true, false)]
    [InlineData(ConnectionState.Connecting, false, false, false)]
    [InlineData(ConnectionState.Connected, true, false, true)]
    [InlineData(ConnectionState.Unauthorized, false, true, true)]
    [InlineData(ConnectionState.ServerUnavailable, false, true, true)]
    public void Ui_gating_helpers(ConnectionState s, bool pdm, bool signIn, bool signOut)
    {
        Assert.Equal(pdm, s.AllowsPdmCommands());
        Assert.Equal(signIn, s.AllowsSignIn());
        Assert.Equal(signOut, s.AllowsSignOut());
    }
}

using System;
using Sandbox;

namespace TTT;

public enum WinType
{
	TimeUp,
	Elimination,
	Objective,
}

public partial class PostRound : BaseState
{
	[Net]
	public Team WinningTeam { get; private set; }

	[Net]
	public WinType WinType { get; private set; }

	public override string Name => "Post";
	public override int Duration => Game.PostRoundTime;

	public PostRound() { }

	public PostRound( Team winningTeam, WinType winType )
	{
		WinningTeam = winningTeam;
		WinType = winType;
	}

	public override void OnPlayerKilled( Player player )
	{
		base.OnPlayerKilled( player );

		player.Confirm( To.Everyone );
	}

	protected override void OnStart()
	{
		base.OnStart();

		Event.Run( TTTEvent.Round.Ended, WinningTeam, WinType );

		if ( !Host.IsServer )
			return;

		Game.Current.TotalRoundsPlayed++;
		RevealEveryone();
	}

	protected override void OnTimeUp()
	{
		base.OnTimeUp();

		bool shouldChangeMap;

		shouldChangeMap = Game.Current.TotalRoundsPlayed >= Game.RoundLimit;
		shouldChangeMap |= Game.Current.RTVCount >= MathF.Round( Client.All.Count * Game.RTVThreshold );

		Game.Current.ChangeState( shouldChangeMap ? new MapSelectionState() : new PreRound() );
	}
}

using System;
using System.Linq;
using Sandbox;
using TTT.UI;
using To = Sandbox.To;

namespace TTT;

public partial class Player
{
	[Net, Local]
	public bool IsForcedSpectator { get; private set; } = false;

	private Player _spectatedPlayer;
	public Player CurrentPlayer
	{
		get => _spectatedPlayer ?? this;
		set
		{
			_spectatedPlayer = value == this ? null : value;
		}
	}
	public bool IsSpectator => Status == PlayerStatus.Spectator;
	public bool IsSpectatingPlayer => _spectatedPlayer.IsValid();
	private int _targetSpectatorIndex = 0;

	[Net, Local] public int PossessionPunches { get; set; }
	public Prop PossessedProp; // serverside & clientside
	private PossessionNameplate _possessionNameplate; // just clientside
	private TimeUntil _timeUntilRecharge = 0;
	private TimeUntil _timeUntilNextPunchAllowed = 0;

	public void ToggleForcedSpectator()
	{
		IsForcedSpectator = !IsForcedSpectator;

		if ( !IsForcedSpectator || !this.IsAlive() )
			return;

		this.Kill();
	}

	public void UpdateSpectatedPlayer( int increment = 0 )
	{
		var oldSpectatedPlayer = CurrentPlayer;
		var players = Utils.GetAlivePlayers();

		if ( players.Count > 0 )
		{
			_targetSpectatorIndex += increment;

			if ( _targetSpectatorIndex >= players.Count )
				_targetSpectatorIndex = 0;
			else if ( _targetSpectatorIndex < 0 )
				_targetSpectatorIndex = players.Count - 1;

			CurrentPlayer = players[_targetSpectatorIndex];
		}

		if ( Camera is ISpectateCamera camera )
			camera.OnUpdateSpectatedPlayer( oldSpectatedPlayer, CurrentPlayer );
	}

	public void MakeSpectator( bool useRagdollCamera = true )
	{
		Controller = null;
		EnableAllCollisions = false;
		EnableDrawing = false;
		EnableTouch = false;
		Health = 0f;
		LifeState = LifeState.Dead;

		if ( Camera is not ISpectateCamera )
			Camera = useRagdollCamera ? new FollowEntityCamera( Corpse ) : new FreeSpectateCamera();

		DelayedDeathCameraChange();
	}

	private async void DelayedDeathCameraChange()
	{
		// If the player is still watching their ragdoll, automatically
		// move them to a free spectate camera.
		await GameTask.DelaySeconds( 2 );

		if ( Camera is FollowEntityCamera cam && cam.FollowedEntity is Corpse )
			Camera = new FreeSpectateCamera();
	}

	public void PossessProp( Prop prop )
	{
		PossessedProp = prop;
		Camera = new FollowEntityCamera( prop );
		prop.Owner = this;

		UpdatePossessionStatus( To.Multiple( Utils.GetDeadClients() ), prop );
	}

	[ClientRpc]
	private void UpdatePossessionStatus( Prop prop )
	{
		if ( prop is null )
		{
			PossessedProp = null;
			_possessionNameplate?.Delete();
			_possessionNameplate = null;

			if ( this == Local.Pawn )
			{
				// we are no longer possessing a prop
				foreach ( var possessionInfo in Local.Hud.ChildrenOfType<PossessionInfo>().ToList() )
				{
					possessionInfo.Delete();
				}
			}
		}
		else
		{
			_possessionNameplate?.Delete(); // there might already be a nameplate from a previous call, remove that
			PossessedProp = prop;
			_possessionNameplate = new PossessionNameplate( this, prop );

			if ( this == Local.Pawn )
				Local.Hud.AddChild<PossessionInfo>();
		}
	}

	[GameEvent.Client.Joined]
	private void SyncPossessionStatus( Client client )
	{
		UpdatePossessionStatus( To.Single( client ), PossessedProp );
	}

	private void ChangeSpectateCamera()
	{
		if ( IsServer && Camera is FollowEntityCamera cam && cam.FollowedEntity is Prop )
		{
			if ( !PossessedProp.IsValid() || Input.Pressed( InputButton.Duck ) )
			{
				Camera = new FreeSpectateCamera();
				PossessedProp = null;

				UpdatePossessionStatus( To.Multiple( Utils.GetDeadClients() ), null );
			}
			else
			{
				HandlePropMovement( PossessedProp.PhysicsBody );
			}

			return;
		}

		if ( !Input.Pressed( InputButton.Jump ) )
			return;

		if ( Camera is FollowEntityCamera || Camera is FirstPersonSpectatorCamera )
		{
			Camera = new FreeSpectateCamera();
			return;
		}

		var spectatablePlayers = Utils.GetAlivePlayers().Count > 0;
		if ( !spectatablePlayers )
		{
			if ( Camera is not FreeSpectateCamera )
				Camera = new FreeSpectateCamera();
			return;
		}

		if ( Camera is FreeSpectateCamera )
			Camera = new ThirdPersonSpectateCamera();
		else if ( Camera is ThirdPersonSpectateCamera )
			Camera = new FirstPersonSpectatorCamera();
	}

	private void HandlePropMovement( PhysicsBody b )
	{
		// reference:
		// https://github.com/Facepunch/garrysmod/blob/master/garrysmod/gamemodes/terrortown/gamemode/propspec.lua#L111

		if ( PossessionPunches <= 0 )
			return;

		if ( Input.Forward + Input.Left == 0f && !Input.Pressed( InputButton.Jump ) )
			return; // no movement

		if ( !_timeUntilNextPunchAllowed ) { return; }

		if ( PossessedProp is IGrabbable grabbable && grabbable.IsHolding )
			return; // can't move if prop is currently being held by a player

		var mass = Math.Min( 150f, b.Mass );
		var force = 110f * 100f;
		var aim = Vector3.Forward * Input.Rotation;
		var mf = mass * force;

		_timeUntilNextPunchAllowed = 0.15f;

		if ( Input.Pressed( InputButton.Jump ) )
		{
			b.ApplyForceAt( b.MassCenter, new Vector3( 0, 0, mf ) );
			_timeUntilNextPunchAllowed = 0.2f; // jump is penalised more
		}
		else if ( Input.Pressed( InputButton.Forward ) || Input.Pressed( InputButton.Back ) )
		{
			b.ApplyForceAt( b.MassCenter, Input.Forward * aim * mf );
		}
		else if ( Input.Pressed( InputButton.Left ) || Input.Pressed( InputButton.Right ) )
		{
			b.ApplyAngularImpulse( new Vector3( 0, 0, Input.Left * 200f * 10f ) );
			b.ApplyForceAt( b.MassCenter, new Vector3( 0, 0, mf / 3f ) );
		}

		PossessionPunches = Math.Max( PossessionPunches - 1, 0 );
	}

	[Event.Tick.Server]
	private void RechargePossessionPunches()
	{
		if ( _timeUntilRecharge && PossessedProp is not null )
		{
			PossessionPunches = Math.Min( PossessionPunches + 1, Game.PropPossessionMaxPunches );
			_timeUntilRecharge = Game.PropPossessionRechargeTime;
		}
		else if ( PossessedProp is null )
		{
			PossessionPunches = 0;
		}
	}

	[GameEvent.Player.StatusChanged]
	private void CheckPossessionStatusAfterStatusChange( Player player, PlayerStatus oldStatus )
	{
		if ( !IsServer || player != this ) { return; }

		if ( player.Status == PlayerStatus.Alive ) // player has come alive
		{
			if ( PossessedProp is not null )
			{
				// player has come alive and is definitely no longer possessing any props
				PossessedProp = null;
				UpdatePossessionStatus( To.Multiple( Utils.GetDeadClients() ), null );
				UpdatePossessionStatus( To.Single( Client ), null ); // also notify itself
			}

			// additionally, now the player has to forget who is possessing what
			foreach ( var possessingPlayer in All.OfType<Player>().Where( p => p.PossessedProp is not null ) )
			{
				possessingPlayer.UpdatePossessionStatus( To.Single( Client ), null );
			}
		}
		else if ( oldStatus == PlayerStatus.Alive )
		{
			// player has just died (or MIA -> Spectator) => needs to know who is possessing what
			foreach ( var somePlayer in All.OfType<Player>().Where( p => p.PossessedProp is not null ) )
			{
				somePlayer.UpdatePossessionStatus( To.Single( Client ), somePlayer.PossessedProp );
			}
		}
	}

	[GameEvent.Player.Killed]
	private static void OnPlayerKilled( Player player )
	{
		if ( !Host.IsClient )
			return;

		var localPlayer = Local.Pawn as Player;

		if ( localPlayer.IsSpectatingPlayer && localPlayer.CurrentPlayer == player )
			localPlayer.Camera = new FreeSpectateCamera();
	}
}
